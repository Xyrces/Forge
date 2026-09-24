#!/usr/bin/env python3
"""Build pinned SWE-Sharp C# evaluator images in local rootless Podman.

The images are tagged with the names the unmodified remote-image evaluator expects.
They are local builds from pinned TestSpec Dockerfiles, not upstream-published images.
Run with the prepared SWE-Sharp virtualenv Python; output stays under private
.portHorizon benchmark state. No runtime containers or host mounts are created.
"""
from __future__ import annotations

import argparse
import fcntl
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
SOURCE_REVISION = "50cc38f602fffe14073953cf128825ea8d92b188"
DEFAULT_SOURCE = ROOT / ".portHorizon/benchmarks/swe-sharp/source" / SOURCE_REVISION
PLATFORM = "linux/amd64"
LABEL = "org.forge.swe-sharp.build-input"
IMAGE_ID = re.compile(r"^(?:sha256:)?[0-9a-f]{64}$")
COMMIT_ID = re.compile(r"^[0-9a-f]{40}$")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def json_digest(value: object) -> str:
    return digest(json.dumps(value, sort_keys=True, separators=(",", ":")).encode())


def normalize_dockerfile(stage: str, source: str) -> str:
    """Only fix the two known upstream C# builder typos; keep all commands intact."""
    if stage not in {"base", "env", "instance"}:
        raise ValueError("invalid stage")
    normalized = source.replace("linux/cs.x86_64", PLATFORM).replace("linux/x86_64", PLATFORM)
    if stage == "env":
        old = "sweb.base.x86_64:latest"
        if normalized.count(old) != 1:
            raise ValueError("unexpected C# environment parent image")
        normalized = normalized.replace(old, "sweb.base.cs.x86_64:latest")
    elif stage == "base":
        # Resolve Docker Hub's official Ubuntu image without Podman's short-name prompt.
        normalized = normalized.replace("ubuntu:22.04", "docker.io/library/ubuntu:22.04")
    if "linux/x86_64" in normalized or "linux/cs.x86_64" in normalized:
        raise ValueError("unsupported platform in Dockerfile")
    return normalized


def pin_parent(dockerfile: str, parent_name: str, parent_id: str) -> str:
    """Bind FROM to the exact inspected local image, preserving every other line."""
    old = f"FROM --platform={PLATFORM} {parent_name}"
    if dockerfile.count(old) != 1 or not IMAGE_ID.fullmatch(parent_id):
        raise ValueError("unexpected parent FROM or image ID")
    return dockerfile.replace(old, f"FROM --platform={PLATFORM} {parent_id}")


def hardened_repo_script(original: str, base_commit: str) -> str:
    """Prune full-clone history in the SAME Docker layer that creates it."""
    if not COMMIT_ID.fullmatch(base_commit):
        raise ValueError("invalid base commit")
    hardening = r'''python3 - <<'PY_FORGE_BASE_HISTORY'
import pathlib
import subprocess

base = "BASE_COMMIT"
def git(*args, check=True):
    result = subprocess.run(["git", *args], cwd="/testbed", text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if check and result.returncode:
        raise RuntimeError(f"git {args[0]} failed: {result.stderr[-300:]}")
    return result

if git("rev-parse", "HEAD").stdout.strip() != base:
    raise RuntimeError("repository is not at the official base commit")
tree = git("rev-parse", "HEAD^{tree}").stdout.strip()
worktree_diff = git("diff", "--binary", "--").stdout
index_diff = git("diff", "--cached", "--binary", "--").stdout
parents = [line.split()[1] for line in git("cat-file", "-p", base).stdout.splitlines()
           if line.startswith("parent ")]
old_tips = git("for-each-ref", "--format=%(objectname)").stdout.splitlines()
git("checkout", "--detach", base)
refs = git("for-each-ref", "--format=%(refname)").stdout.splitlines()
for ref in refs:
    git("update-ref", "-d", ref)
git_dir = pathlib.Path(git("rev-parse", "--absolute-git-dir").stdout.strip())
(git_dir / "shallow").write_text(base + "\n")
git("reflog", "expire", "--expire=now", "--expire-unreachable=now", "--all")
git("gc", "--prune=now")
git("prune", "--expire=now")
if git("rev-parse", "HEAD").stdout.strip() != base:
    raise RuntimeError("base commit identity changed")
if git("rev-parse", "HEAD^{tree}").stdout.strip() != tree:
    raise RuntimeError("base tree changed")
if git("rev-list", "HEAD").stdout.splitlines() != [base]:
    raise RuntimeError("non-base commit remains visible")
if git("for-each-ref", "--format=%(refname)").stdout.strip() or git("remote").stdout.strip():
    raise RuntimeError("repository refs or remotes remain")
if (git("diff", "--binary", "--").stdout != worktree_diff
        or git("diff", "--cached", "--binary", "--").stdout != index_diff):
    raise RuntimeError("official setup's tracked changes were not preserved")
for candidate in set(parents + old_tips) - {base}:
    if git("cat-file", "-e", candidate, check=False).returncode == 0:
        raise RuntimeError("old or future commit object remains available")
if git("fsck", "--full", "--unreachable", "--no-reflogs").stdout.strip():
    raise RuntimeError("unreachable Git objects remain")
PY_FORGE_BASE_HISTORY
'''.replace("BASE_COMMIT", base_commit)
    return original.rstrip("\n") + "\n" + hardening


def run(command: list[str], timeout: int = 120, log: Path | None = None) -> str:
    if log is None:
        result = subprocess.run(command, text=True, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=timeout, check=False)
        if result.returncode:
            raise RuntimeError(f"command failed ({result.returncode}): {command[:3]}; {result.stderr[-500:]}")
        return result.stdout.strip()
    with log.open("wb") as stream:
        process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        try:
            status = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired as error:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
            raise RuntimeError(f"image build timed out after {timeout}s; log: {log}") from error
        except BaseException:
            # A Ctrl-C must not strand a build process with network access.
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
            raise
        if status:
            raise RuntimeError(f"image build failed ({status}); log: {log}")
    return ""


def inspect_image(name: str) -> dict | None:
    result = subprocess.run(["podman", "image", "inspect", name], text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            timeout=120, check=False)
    if result.returncode:
        if ("no such image" in result.stderr.lower() or "not found" in result.stderr.lower()
                or "image not known" in result.stderr.lower()):
            return None
        raise RuntimeError(f"podman image inspect failed for {name}: {result.stderr[-500:]}")
    images = json.loads(result.stdout)
    if not isinstance(images, list) or len(images) != 1 or not IMAGE_ID.fullmatch(images[0].get("Id", "")):
        raise ValueError(f"invalid Podman image inspection for {name}")
    return images[0]


def image_id(image: dict) -> str:
    value = image["Id"]
    return value if value.startswith("sha256:") else f"sha256:{value}"


def verify_source(source: Path) -> dict:
    metadata = json.loads((source / "source.json").read_text())
    if metadata.get("sourceRevision") != SOURCE_REVISION or not isinstance(metadata.get("files"), dict):
        raise ValueError("unexpected SWE-Sharp source revision")
    for relative, expected in metadata["files"].items():
        path = source / relative
        if path.resolve().is_relative_to(source.resolve()) is False or digest(path.read_bytes()) != expected:
            raise ValueError(f"modified upstream source: {relative}")
    return metadata


def load_specs(source: Path, dataset: Path, instance_id: str | None):
    sys.path.insert(0, str(source / "harness"))
    from swe_sharp_bench.test_spec import make_test_spec

    rows = json.loads(dataset.read_text())
    if not isinstance(rows, list) or not rows:
        raise ValueError("dataset must be a nonempty list")
    ids = [row["instance_id"] for row in rows]
    if len(ids) != len(set(ids)):
        raise ValueError("duplicate instance IDs")
    if instance_id:
        rows = [row for row in rows if row["instance_id"] == instance_id]
        if len(rows) != 1:
            raise ValueError("requested instance ID is absent")
    specs = [make_test_spec(row, namespace="swebcs") for row in rows]
    for spec, row in zip(specs, rows):
        base_commit = row.get("base_commit")
        if not isinstance(base_commit, str) or not COMMIT_ID.fullmatch(base_commit):
            raise ValueError("invalid dataset base commit")
        spec.benchmark_base_commit = base_commit
    if any(spec.language != "cs" or spec.arch != "x86_64" for spec in specs):
        raise ValueError("this builder supports C# x86_64 images only")
    return specs


def build_stage(stage: str, name: str, dockerfile: str, scripts: dict[str, str],
                parent_id: str, output: Path, parent_name: str | None = None,
                progress=None, replace_owned: bool = False) -> dict:
    generated_hash = digest(dockerfile.encode())
    if parent_name is not None:
        dockerfile = pin_parent(dockerfile, parent_name, parent_id)
    inputs = {"stage": stage, "name": name, "platform": PLATFORM,
              "parentId": parent_id, "dockerfileSha256": digest(dockerfile.encode()),
              "upstreamGeneratedDockerfileSha256": generated_hash,
              "scriptsSha256": {key: digest(value.encode()) for key, value in sorted(scripts.items())}}
    fingerprint = json_digest(inputs)
    if progress is not None:
        progress({**inputs, "inputSha256": fingerprint})
    if parent_name is not None:
        parent = inspect_image(parent_name)
        if parent is None or image_id(parent) != parent_id:
            raise RuntimeError(f"parent image changed before build: {parent_name}")
    existing = inspect_image(name)
    replaced = None
    if existing is not None:
        labels = existing.get("Labels") or existing.get("Config", {}).get("Labels") or {}
        if labels.get(LABEL) != fingerprint:
            old_fingerprint = labels.get(LABEL, "")
            if not replace_owned or not re.fullmatch(r"[0-9a-f]{64}", old_fingerprint):
                raise ValueError(f"existing image has different or absent build provenance: {name}")
            replaced = {"imageId": image_id(existing), "inputSha256": old_fingerprint,
                        "untaggedName": name}
            if progress is not None:
                progress({**inputs, "inputSha256": fingerprint, "replacing": replaced})
            run(["podman", "untag", name])
        else:
            return {**inputs, "inputSha256": fingerprint, "imageId": image_id(existing), "reused": True}
    directory = output / "contexts" / fingerprint
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "Dockerfile").write_text(dockerfile)
    for filename, content in scripts.items():
        if filename not in {"setup_env.sh", "setup_repo.sh"}:
            raise ValueError("unexpected setup script name")
        (directory / filename).write_text(content)
    command = ["podman", "build", "--platform", PLATFORM, "--pull=never",
               "--label", f"{LABEL}={fingerprint}", "--tag", name,
               "--file", str(directory / "Dockerfile"), str(directory)]
    run(command, timeout=7200, log=output / f"{stage}-{fingerprint[:16]}.log")
    if parent_name is not None:
        parent = inspect_image(parent_name)
        if parent is None or image_id(parent) != parent_id:
            raise RuntimeError(f"parent image changed during build: {parent_name}")
    built = inspect_image(name)
    if built is None:
        raise RuntimeError(f"image absent after successful build: {name}")
    labels = built.get("Labels") or built.get("Config", {}).get("Labels") or {}
    if labels.get(LABEL) != fingerprint:
        raise RuntimeError(f"built image missing provenance label: {name}")
    return {**inputs, "inputSha256": fingerprint, "imageId": image_id(built),
            "reused": False, "replaced": replaced}


def save_manifest(path: Path, data: dict) -> None:
    temporary = path.with_suffix(".json.tmp")
    with temporary.open("w") as stream:
        json.dump(data, stream, indent=2, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def _build_unlocked(dataset: Path, source: Path, output: Path, instance_id: str | None = None,
                    replace_owned: bool = False) -> dict:
    private_root = (ROOT / ".portHorizon/benchmarks/swe-sharp").resolve()
    output = output.resolve()
    if not output.is_relative_to(private_root):
        raise ValueError("image output must stay under private SWE-Sharp benchmark state")
    if not dataset.is_file() or not source.is_dir():
        raise ValueError("dataset or pinned source is missing")
    source_metadata = verify_source(source)
    specs = load_specs(source, dataset, instance_id)
    output.mkdir(parents=True, exist_ok=True)
    manifest = {"provenance": "locally built from pinned SWE-Sharp TestSpec; not a published upstream image",
                "sourceRevision": SOURCE_REVISION, "sourceManifestSha256": json_digest(source_metadata),
                "sourceFilesSha256": source_metadata["files"],
                "swebenchVersion": importlib.metadata.version("swebench"),
                "datasetSha256": digest(dataset.read_bytes()), "builderSha256": digest(Path(__file__).read_bytes()),
                "platform": PLATFORM, "status": "building", "stages": {}}
    manifest_path = output / "manifest.json"

    def progress(inputs):
        manifest["pendingStage"] = inputs
        save_manifest(manifest_path, manifest)

    def record(result, name):
        manifest["stages"][name] = result
        manifest.pop("pendingStage", None)
        save_manifest(manifest_path, manifest)
    # Keep the local Ubuntu parent fixed during this run. Never silently refresh a parent.
    ubuntu = inspect_image("docker.io/library/ubuntu:22.04")
    if ubuntu is None:
        run(["podman", "pull", "--platform", PLATFORM, "docker.io/library/ubuntu:22.04"], timeout=900,
            log=output / "ubuntu-pull.log")
        ubuntu = inspect_image("docker.io/library/ubuntu:22.04")
    if ubuntu is None:
        raise RuntimeError("Ubuntu base image unavailable")
    manifest["ubuntuParentId"] = image_id(ubuntu)
    # All selected specs should share this C# base Dockerfile and tag.
    bases = {spec.base_image_key: normalize_dockerfile("base", spec.base_dockerfile) for spec in specs}
    if len(bases) != 1:
        raise ValueError("unexpected multiple C# base images")
    try:
        for name, dockerfile in bases.items():
            record(build_stage("base", name, dockerfile, {}, image_id(ubuntu), output,
                               parent_name="docker.io/library/ubuntu:22.04", progress=progress,
                               replace_owned=replace_owned), name)
        for spec in specs:
            base = manifest["stages"][spec.base_image_key]
            env_name = spec.env_image_key
            env_dockerfile = normalize_dockerfile("env", spec.env_dockerfile)
            if env_name not in manifest["stages"]:
                record(build_stage(
                    "env", env_name, env_dockerfile, {"setup_env.sh": spec.setup_env_script},
                    base["imageId"], output, parent_name=spec.base_image_key,
                    progress=progress, replace_owned=replace_owned), env_name)
            environment = manifest["stages"][env_name]
            name = spec.instance_image_key
            record(build_stage(
                "instance", name, normalize_dockerfile("instance", spec.instance_dockerfile),
                {"setup_repo.sh": hardened_repo_script(spec.install_repo_script,
                                                        spec.benchmark_base_commit)},
                environment["imageId"], output, parent_name=env_name, progress=progress,
                replace_owned=replace_owned), name)
            print(f"ready {spec.instance_id}: {name} {manifest['stages'][name]['imageId']}", flush=True)
    except BaseException as error:
        manifest["status"] = "interrupted" if isinstance(error, KeyboardInterrupt) else "failed"
        manifest["failureType"] = type(error).__name__
        save_manifest(manifest_path, manifest)
        raise
    manifest["status"] = "complete"
    save_manifest(manifest_path, manifest)
    return manifest


def build(dataset: Path, source: Path, output: Path, instance_id: str | None = None,
          replace_owned: bool = False) -> dict:
    private_root = (ROOT / ".portHorizon/benchmarks/swe-sharp").resolve()
    if not output.resolve().is_relative_to(private_root):
        raise ValueError("image output must stay under private SWE-Sharp benchmark state")
    private_root.mkdir(parents=True, exist_ok=True)
    with (private_root / ".image-builder.lock").open("a+") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise RuntimeError("another SWE-Sharp image builder is running") from error
        return _build_unlocked(dataset, source, output, instance_id, replace_owned)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", type=Path, required=True)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--instance-id", help="build one selected instance for calibration")
    parser.add_argument("--replace-owned-images", action="store_true",
                        help="untag and rebuild only images carrying this builder's provenance label")
    args = parser.parse_args()
    build(args.dataset, args.source, args.output, args.instance_id, args.replace_owned_images)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
