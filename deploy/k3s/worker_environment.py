#!/usr/bin/env python3
"""Build a project's configured Dockerfile on the Forge single-node K3s host."""
import argparse
import hashlib
import ipaddress
import json
import os
import re
import subprocess
import sys
import tarfile
import tempfile
import uuid
from pathlib import Path, PurePosixPath
from urllib import error, parse, request


def run(args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


def output(args):
    return run(args, capture_output=True, text=True).stdout.strip()


class NoRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Dashboard:
    def __init__(self, url, project, token):
        address = parse.urlsplit(url)
        try:
            loopback = ipaddress.ip_address(address.hostname or "").is_loopback
        except ValueError:
            loopback = address.hostname == "localhost"
        if (not address.hostname or address.username is not None or address.password is not None
                or address.query or address.fragment or
                not (address.scheme == "https" or (address.scheme == "http" and loopback))):
            raise ValueError("Use an HTTPS dashboard URL or an HTTP loopback tunnel.")
        self.base = url.rstrip("/") + "/api/projects/" + parse.quote(project, safe="")
        self.opener = request.build_opener(NoRedirect())
        self.token = token

    def call(self, path="", payload=None):
        headers = {"Authorization": "Bearer " + self.token}
        data = None
        if payload is not None:
            data = json.dumps(payload).encode()
            headers["Content-Type"] = "application/json"
        req = request.Request(self.base + path, data=data, headers=headers)
        try:
            with self.opener.open(req, timeout=30) as response:
                return json.load(response)
        except error.HTTPError as exc:
            if exc.code == 409:
                raise ValueError("Settings changed during qualification. Reload and build the current definition.") from exc
            raise ValueError(f"Dashboard returned HTTP {exc.code} for {path or 'project'}.") from exc


def repository_id(url):
    # Allow GitHub's HTTPS and SSH clone spellings to identify the same repository.
    match = re.fullmatch(r"(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)([\w.-]+/[\w.-]+?)(?:\.git)?/?", url, re.I)
    return match.group(1).lower() if match else url


def snapshot(repo, project, definition, dest):
    git = ["git", "--no-replace-objects", "-C", str(repo)]
    if repository_id(output(git + ["remote", "get-url", "origin"])) != repository_id(project["repoUrl"]):
        raise ValueError("The checkout's origin differs from this project's registered repository.")
    branch = project["defaultBranch"]
    run(git + ["check-ref-format", "refs/heads/" + branch], stdout=subprocess.DEVNULL)
    ref = "refs/heads/" + branch
    rows = output(git + ["ls-remote", "--exit-code", "origin", ref]).splitlines()
    commits = [parts[0] for row in rows if len(parts := row.split()) == 2 and parts[1] == ref]
    if len(commits) != 1 or not re.fullmatch("[0-9a-f]{40}", commits[0]):
        raise ValueError("Could not resolve the project's default branch to one commit.")
    sha = commits[0]
    run(git + ["fetch", "--no-tags", "--no-write-fetch-head", "origin", sha], stdout=subprocess.DEVNULL)
    context = definition["buildContext"]
    dockerfile = definition["dockerfilePath"]
    for name in (dockerfile, context):
        if name != "." and (not name or any(p in ("", ".", "..") for p in name.split("/"))
                            or "\\" in name or ":" in name or any(ord(c) < 32 for c in name)):
            raise ValueError("Invalid repository-relative build path.")
    relative = PurePosixPath(dockerfile).relative_to(PurePosixPath(context))
    tree = sha if context == "." else sha + ":" + context
    archive = dest.parent / "source.tar"
    run(git + ["archive", "--format=tar", "--output", str(archive), tree])
    dest.mkdir()
    with tarfile.open(archive) as source:
        source.extractall(dest, filter="data")
    archive.unlink()
    candidate = dest / relative
    if candidate.resolve() != candidate or not candidate.is_file():
        raise ValueError("Select a tracked regular Dockerfile within the build context.")
    return sha, candidate


def qualify(dashboard, project_id, repo, check):
    settings = dashboard.call("/worker-environment")
    if not settings.get("dockerfilePath"):
        raise ValueError("Select a Dockerfile in the project's worker environment first.")
    # A repository/default-branch change invalidates this settings revision. Read
    # the source after the revision so an overlapping qualification loses its CAS.
    project = dashboard.call()
    if os.geteuid() == 0 or output(["podman", "info", "--format", "{{.Host.Security.Rootless}}"]) != "true":
        raise ValueError("Build the project image using rootless Podman as a regular user.")
    with tempfile.TemporaryDirectory(prefix="forge-project-image-") as temporary:
        stage = Path(temporary).resolve()
        context = stage / "context"
        sha, dockerfile = snapshot(repo, project, settings, context)
        repository = "localhost/forge-toolchain-" + hashlib.sha256(project_id.encode()).hexdigest()[:16]
        tag = repository + ":" + sha[:12] + "-" + uuid.uuid4().hex[:8]
        print(f"Building {settings['dockerfilePath']} at {sha}", flush=True)
        run(["podman", "build", "--http-proxy=false", "--layers", "-t", tag, "-f", str(dockerfile), str(context)])
        image_id = output(["podman", "image", "inspect", "--format", "{{.Id}}", tag])
        run(["podman", "run", "--rm", "--network=none", "--http-proxy=false", "--read-only",
             "--user", "10001:10001", "--cap-drop=ALL", "--security-opt=no-new-privileges",
             "--tmpfs", "/tmp:rw,mode=1777", "--workdir", "/tmp", "--env", "HOME=/tmp",
             "--entrypoint", "/bin/bash", image_id, "-ec",
             'command -v git >/dev/null\ntest -x /bin/sh\ntest -w /tmp\n' + (check or "true")])
        archive = stage / "image.oci"
        run(["podman", "save", "--format", "oci-archive", "--output", str(archive), tag])
        ctr = ["sudo", "-n", "k3s", "ctr", "images"]
        run(ctr + ["import", "--digests", str(archive)])
        entries = [row.split() for row in output(ctr + ["ls", f"name=={tag}"]).splitlines()]
        digests = [row[2] for row in entries if len(row) >= 3 and row[0] == tag]
        if len(digests) != 1 or not re.fullmatch("sha256:[a-f0-9]{64}", digests[0]):
            raise ValueError("K3s did not return a valid image digest.")
        pinned = repository + "@" + digests[0]
        # An OCI import may register digest aliases under containerd's generated
        # import name. Give CRI the exact repository-qualified name used by Pods.
        run(ctr + ["tag", "--force", tag, pinned])
        # CRI resolves Pod image references by repository + digest, including local-only images.
        run(["sudo", "-n", "k3s", "crictl", "inspecti", pinned], stdout=subprocess.DEVNULL)
        dashboard.call("/worker-environment/qualify", {
            "expectedRevision": settings["revision"], "definitionRevision": settings["definitionRevision"],
            "image": pinned, "sourceRevision": sha})
        print(f"Activated {pinned} from {sha}.")
        return pinned


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True)
    parser.add_argument("--repo", required=True, type=Path, help="Checkout with access to the registered origin")
    parser.add_argument("--dashboard", required=True)
    parser.add_argument("--token-file", default=os.environ.get("FORGE_CONTROLLER_TOKEN_FILE"), type=Path)
    parser.add_argument("--check", help="Additional Bash qualification command, run offline inside the image")
    args = parser.parse_args()
    if not args.token_file:
        parser.error("Supply --token-file or FORGE_CONTROLLER_TOKEN_FILE.")
    try:
        token = args.token_file.read_text().strip()
        if not token or any(c.isspace() for c in token):
            raise ValueError("Invalid dashboard token file.")
        qualify(Dashboard(args.dashboard, args.project, token), args.project, args.repo, args.check)
    except (OSError, ValueError, KeyError, tarfile.TarError, subprocess.CalledProcessError) as exc:
        sys.exit("Qualification failed: " + str(exc))


if __name__ == "__main__":
    main()
