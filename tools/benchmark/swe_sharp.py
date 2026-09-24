#!/usr/bin/env python3
"""Pinned SWE-Sharp data preparation and trusted, separate official evaluation.

This supervisor never calls a model. Keep source/control/evaluation directories
outside agent sandboxes. Only the prepared agent/ directory is an agent input.
"""
from __future__ import annotations

import argparse
import ast
import csv
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import urllib.request
import uuid

SOURCE_REVISION = "50cc38f602fffe14073953cf128825ea8d92b188"
PREFIX = "misc/SWE-Sharp-Bench/"
DATASET_FILE = "data/benchmark/swe-sharp-bench.csv"
ROOT = Path(__file__).resolve().parents[2]
DEFAULT_ROOT = ROOT / ".portHorizon/benchmarks/swe-sharp"
SHA = re.compile(r"[0-9a-f]{40}")
ID = re.compile(r"[A-Za-z0-9_-]+")
REPO = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")
FIELDS = {"repo", "instance_id", "base_commit", "patch", "test_patch", "problem_statement",
          "hints_text", "created_at", "version", "FAIL_TO_PASS", "PASS_TO_PASS"}


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def unique_object(pairs):
    out = {}
    for key, value in pairs:
        if key in out:
            raise ValueError(f"duplicate JSON field: {key}")
        out[key] = value
    return out


def read_json(path):
    return json.loads(Path(path).read_text(), object_pairs_hook=unique_object)


def write_json(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    with temp.open("w") as f:
        json.dump(data, f, indent=2, allow_nan=False)
        f.write("\n")
        f.flush()
        os.fsync(f.fileno())
    temp.replace(path)


def safe_path(name):
    p = PurePosixPath(name)
    if (not name or p.is_absolute() or any(x in ("", ".", "..", ".git", ".portHorizon") for x in name.split("/"))
            or "\\" in name or any(ord(c) < 32 for c in name)):
        raise ValueError("unsafe repository path")
    return p


def download(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Forge-benchmark-setup"})
    with urllib.request.urlopen(request, timeout=90) as response:
        data = response.read(20_000_001)
    if len(data) > 20_000_000:
        raise ValueError("upstream file exceeds size limit")
    return data


def fetch_source(output):
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    tree = json.loads(download(f"https://api.github.com/repos/microsoft/prose/git/trees/{SOURCE_REVISION}?recursive=1"))
    if tree.get("truncated"):
        raise ValueError("upstream tree response is truncated")
    files = {}
    for entry in tree["tree"]:
        full = entry["path"]
        if entry["type"] != "blob" or not full.startswith(PREFIX):
            continue
        rel = full[len(PREFIX):]
        if not (rel.startswith("harness/") or rel in (DATASET_FILE, "README.md", "LICENSE")):
            continue
        safe_path(rel)
        content = download(f"https://raw.githubusercontent.com/microsoft/prose/{SOURCE_REVISION}/{full}")
        blob = hashlib.sha1(f"blob {len(content)}\0".encode() + content).hexdigest()
        if blob != entry["sha"]:
            raise ValueError("downloaded source does not match pinned Git blob")
        path = output / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        files[rel] = digest(path)
    if DATASET_FILE not in files or "harness/swe_sharp_bench/cli.py" not in files:
        raise ValueError("required upstream source missing")
    write_json(output / "source.json", {"sourceRevision": SOURCE_REVISION, "files": files})
    return output


def verify_source(source):
    source = Path(source).resolve()
    metadata = read_json(source / "source.json")
    if metadata.get("sourceRevision") != SOURCE_REVISION or not metadata.get("files"):
        raise ValueError("unexpected upstream revision/source manifest")
    for name, expected in metadata["files"].items():
        safe_path(name)
        if digest(source / name) != expected:
            raise ValueError(f"modified upstream file: {name}")
    return metadata


def load_dataset(path):
    csv.field_size_limit(10_000_000)
    with Path(path).open(newline="", encoding="utf-8-sig") as f:
        reader = csv.DictReader(f)
        if set(reader.fieldnames or ()) != FIELDS:
            raise ValueError("unexpected SWE-Sharp CSV schema")
        rows = list(reader)
    indexed = {}
    for row in rows:
        ident = row["instance_id"]
        if (not ID.fullmatch(ident) or ident in indexed or not REPO.fullmatch(row["repo"])
                or not SHA.fullmatch(row["base_commit"])):
            raise ValueError("invalid/duplicate task identity")
        for key in ("FAIL_TO_PASS", "PASS_TO_PASS"):
            # Upstream CSV uses Python list literals, not JSON. literal_eval is
            # deliberately restricted; never execute dataset values as Python.
            value = ast.literal_eval(row[key])
            if (not isinstance(value, list) or any(not isinstance(t, str) or not t for t in value)
                    or len(value) != len(set(value))):
                raise ValueError(f"invalid test declaration for {ident}")
            row[key] = value
        indexed[ident] = row
    return indexed


def git_env():
    env = {k: os.environ[k] for k in ("PATH", "LANG", "TMPDIR") if k in os.environ}
    env.update({"GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull, "GIT_TERMINAL_PROMPT": "0",
                "GIT_AUTHOR_NAME": "Forge benchmark", "GIT_AUTHOR_EMAIL": "benchmark@localhost",
                "GIT_COMMITTER_NAME": "Forge benchmark", "GIT_COMMITTER_EMAIL": "benchmark@localhost",
                "GIT_AUTHOR_DATE": "2000-01-01T00:00:00Z", "GIT_COMMITTER_DATE": "2000-01-01T00:00:00Z"})
    return env


def git(directory, *args, binary=False):
    result = subprocess.run(["git", "-c", "core.hooksPath=/dev/null", "-c", "credential.helper=", *args],
                            cwd=directory, env=git_env(), check=True, capture_output=True, timeout=300)
    return result.stdout if binary else result.stdout.decode().strip()


def snapshot_repository(row, cache, target):
    """Fetch base only, copy blobs, reinitialize: no history, remotes or solutions."""
    cache.mkdir(parents=True, exist_ok=False)
    git(cache, "init", "-q")
    git(cache, "fetch", "--depth=1", "--no-tags", f"https://github.com/{row['repo']}.git", row["base_commit"])
    if git(cache, "rev-parse", "FETCH_HEAD") != row["base_commit"]:
        raise ValueError("fetched commit identity differs")
    entries = git(cache, "ls-tree", "-rz", row["base_commit"], binary=True).split(b"\0")
    target.mkdir(parents=True, exist_ok=False)
    total = 0
    index_entries = []
    for entry in entries:
        if not entry:
            continue
        meta, name = entry.split(b"\t", 1)
        mode, kind, blob = meta.decode().split()
        relative = safe_path(name.decode("utf-8"))
        if kind != "blob" or mode not in ("100644", "100755"):
            raise ValueError("symlinks and submodules require a separately validated importer")
        content = git(cache, "cat-file", "blob", blob, binary=True)
        total += len(content)
        if total > 250_000_000:
            raise ValueError("repository snapshot exceeds 250 MB limit")
        path = target / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        path.chmod(0o755 if mode == "100755" else 0o644)
        index_entries.append((mode, blob, name.decode("utf-8")))
    git(target, "init", "-q", "-b", "main")
    # git add would apply .gitattributes clean filters/line-ending conversions
    # again. Import exact blobs/index modes so the original tree is preserved.
    for offset in range(0, len(index_entries), 100):
        batch = index_entries[offset:offset + 100]
        blobs = git(target, "hash-object", "-w", "--no-filters", "--", *(x[2] for x in batch)).splitlines()
        if blobs != [x[1] for x in batch]:
            raise ValueError("snapshot blob identity differs")
    index_data = b"".join(f"{mode} {blob}\t{name}\0".encode() for mode, blob, name in index_entries)
    subprocess.run(["git", "update-index", "-z", "--index-info"], input=index_data,
                   cwd=target, env=git_env(), check=True, capture_output=True, timeout=120)
    git(target, "commit", "-q", "-m", "Benchmark base snapshot")
    tree = git(target, "rev-parse", "HEAD^{tree}")
    if tree != git(cache, "rev-parse", row["base_commit"] + "^{tree}"):
        raise ValueError("snapshot tree differs from pinned upstream base")
    return git(target, "rev-parse", "HEAD"), tree


def prepare(source, selection, output):
    source = Path(source).resolve()
    verify_source(source)
    selected = read_json(selection)
    ids = selected.get("instanceIds")
    if (selected.get("sourceRevision") != SOURCE_REVISION or not isinstance(ids, list) or not ids
            or any(not isinstance(i, str) for i in ids) or len(ids) != len(set(ids))):
        raise ValueError("invalid selection")
    dataset_file = source / DATASET_FILE
    dataset = load_dataset(dataset_file)
    rows = [dataset[i] for i in ids]
    if any(not r[key].strip() for r in rows for key in ("patch", "test_patch", "problem_statement", "version")):
        raise ValueError("selected tasks require nonempty problem, patches, and version")
    if any(not r["FAIL_TO_PASS"] or not r["PASS_TO_PASS"] for r in rows):
        raise ValueError("pilot tasks must have both regression and repair tests")
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    agent = output / "agent"
    control = output / "control"
    control.mkdir()
    write_json(control / "dataset.json", rows)
    cases, exclusions = [], []
    for row in rows:
        ident = row["instance_id"]
        try:
            target = agent / ident / "repository"
            base, tree = snapshot_repository(row, control / "fetch" / ident, target)
            reference = control / (ident + "-scope.patch")
            reference.write_text(row["patch"])
            validate_candidate_patch(reference, row, target)
            case_path = agent / ident / "case.json"
            write_json(case_path, {"id": ident, "title": f"SWE-Sharp: {ident}",
                "prompt": row["problem_statement"] + "\n\nBenchmark scope: change production C# source (.cs), documentation (.md), or API text (.txt). Do not change tests, hidden files, project/build configuration, or scripts. Independent tests will evaluate the exported patch.",
                "repositoryPath": str(target), "baseCommit": base})
            cases.append({"id": ident, "repo": row["repo"], "upstreamBaseCommit": row["base_commit"],
                          "baseCommit": base, "snapshotTree": tree, "casePath": str(case_path), "caseSha256": digest(case_path)})
            print(f"Prepared {ident}", flush=True)
        except (ValueError, OSError, subprocess.SubprocessError) as error:
            # Keep partial data for diagnosis in control, never silently choose
            # another task based on observed model performance.
            if (agent / ident).exists():
                shutil.move(str(agent / ident), str(control / (ident + "-rejected")))
            exclusions.append({"id": ident, "reason": str(error) if isinstance(error, ValueError) else type(error).__name__})
            print(f"Environment preparation rejected {ident}: {exclusions[-1]['reason']}", flush=True)
    manifest = {"schemaVersion": 1, "dataset": "swe-sharp-bench", "sourceRevision": SOURCE_REVISION,
                "datasetSha256": digest(dataset_file), "cases": cases}
    write_json(agent / "manifest.json", manifest)
    prepared_ids = {case["id"] for case in cases}
    write_json(control / "dataset.json", [r for r in rows if r["instance_id"] in prepared_ids])
    write_json(control / "prepared.json", {"sourceRoot": str(source), "manifestPath": str(agent / "manifest.json"),
        "datasetPath": str(control / "dataset.json"), "datasetSubsetSha256": digest(control / "dataset.json"),
        "manifestSha256": digest(agent / "manifest.json"), "selection": selected, "exclusions": exclusions})
    return output


def validate_manifest(path):
    manifest = read_json(path)
    if (manifest.get("schemaVersion") != 1 or manifest.get("dataset") != "swe-sharp-bench"
            or manifest.get("sourceRevision") != SOURCE_REVISION or not manifest.get("cases")):
        raise ValueError("invalid external manifest")
    ids = set()
    for case in manifest["cases"]:
        if not ID.fullmatch(case["id"]) or case["id"] in ids:
            raise ValueError("invalid/duplicate manifest case")
        ids.add(case["id"])
        if digest(case["casePath"]) != case["caseSha256"]:
            raise ValueError("agent case definition changed")
        data = read_json(case["casePath"])
        if data["id"] != case["id"] or data["baseCommit"] != case["baseCommit"]:
            raise ValueError("agent case identity changed")
        repo = Path(data["repositoryPath"])
        if (git(repo, "rev-parse", "HEAD") != case["baseCommit"]
                or git(repo, "rev-parse", "HEAD^{tree}") != case["snapshotTree"]
                or git(repo, "status", "--porcelain", "--untracked-files=all")
                or git(repo, "ls-files", "--others", "--ignored", "--exclude-standard")
                or git(repo, "rev-list", "--all", "--count") != "1" or git(repo, "remote")):
            raise ValueError("agent repository is no longer a clean, single-commit snapshot")
    return manifest


def validate_preflight(manifest_path, receipt_path):
    manifest = validate_manifest(manifest_path)
    receipt = read_json(receipt_path)
    ids = [c["id"] for c in manifest["cases"]]
    baselines = receipt.get("baselines")
    negative_controls = receipt.get("negativeControls")
    images = receipt.get("imageIds")
    if (receipt.get("success") is not True or receipt.get("schemaVersion") != 1
            or receipt.get("manifestSha256") != digest(manifest_path)
            or receipt.get("datasetSha256") != manifest["datasetSha256"]
            or receipt.get("sourceRevision") != SOURCE_REVISION
            or receipt.get("caseIds") != ids
            or not isinstance(receipt.get("evaluatorFingerprint"), str)
            or not re.fullmatch(r"[0-9a-f]{64}", receipt["evaluatorFingerprint"])
            or not isinstance(images, dict) or set(images) != set(ids)
            or any(not isinstance(value, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", value)
                   for value in images.values())
            or not isinstance(baselines, dict) or set(baselines) != set(ids)
            or any(baselines[i] != {"gold": True, "empty": True} for i in ids)
            or not isinstance(negative_controls, dict) or set(negative_controls) != set(ids)
            or any(not valid_negative_control_receipt(negative_controls[i]) for i in ids)
            or receipt.get("errors") != []):
        raise ValueError("official gold/empty environment preflight is missing, failed, or stale")
    return {key: receipt[key] for key in ("manifestSha256", "datasetSha256", "sourceRevision", "caseIds", "evaluatorFingerprint", "imageIds")} | {"receiptSha256": digest(receipt_path)}


def valid_negative_control_receipt(value):
    return (isinstance(value, dict) and set(value) == {"kind", "signatureSha256"}
            and value.get("kind") in {"declared-test-failure", "hidden-test-compile-failure"}
            and isinstance(value.get("signatureSha256"), str)
            and re.fullmatch(r"[0-9a-f]{64}", value["signatureSha256"]) is not None)


def check_official_report(report, row, expected_resolved):
    """Do not trust a process exit or resolved flag alone: reconcile every test."""
    ident = row["instance_id"]
    if set(report) != {ident}:
        raise ValueError("official report instance identity mismatch")
    value = report[ident]
    if (value.get("patch_is_None") is not False or value.get("patch_exists") is not True
            or value.get("patch_successfully_applied") is not True
            or value.get("resolved") is not expected_resolved):
        raise ValueError("official patch/test execution did not match expected outcome")
    statuses = value.get("tests_status", {})
    observed = value.get("forge_observed_tests", {})
    expected_tests = set(row["FAIL_TO_PASS"]) | set(row["PASS_TO_PASS"])
    if (value.get("forge_log_parse_success") is not True
            or value.get("forge_independent_trx_valid") is not True or set(observed) != expected_tests
            or any(status not in ("PASSED", "FAILED") for status in observed.values())):
        raise ValueError("declared tests were missing, skipped, or errored in parsed test output")
    failed_repair = False
    for key in ("FAIL_TO_PASS", "PASS_TO_PASS"):
        group = statuses.get(key, {})
        success, failure = group.get("success"), group.get("failure")
        if (not isinstance(success, list) or not isinstance(failure, list)
                or any(not isinstance(t, str) for t in success + failure)
                or len(success + failure) != len(set(success + failure))
                or set(success + failure) != set(row[key])):
            raise ValueError("official report has missing/duplicate/unexpected test results")
        if failure and (expected_resolved or key == "PASS_TO_PASS"):
            raise ValueError("repair or regression tests failed")
        if any(observed[t] != "PASSED" for t in success) or any(observed[t] != "FAILED" for t in failure):
            raise ValueError("parsed output contradicts official summary")
        failed_repair |= key == "FAIL_TO_PASS" and bool(failure)
    if not expected_resolved and not failed_repair:
        raise ValueError("baseline did not reproduce the defect")
    return True


def _control_signature(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def _hidden_test_paths(row):
    paths = set()
    for name in re.findall(r"^\+\+\+ b/(.+)$", row["test_patch"], re.MULTILINE):
        paths.add(str(safe_path(name)))
    if not paths:
        raise ValueError("hidden test patch has no changed paths")
    return paths


def classify_empty_baseline(report, row):
    """Accept an ordinary failing test or a tightly-scoped hidden-test compile failure.

    This function is used only for the trusted empty control. Candidate and gold
    evaluation continue to require every declared test to run and pass.
    """
    try:
        check_official_report(report, row, False)
    except ValueError as ordinary_error:
        ident = row["instance_id"]
        if set(report) != {ident}:
            raise ordinary_error
        value = report[ident]
        if (value.get("patch_is_None") is not False or value.get("patch_exists") is not True
                or value.get("patch_successfully_applied") is not True or value.get("resolved") is not False):
            raise ordinary_error
        evidence = value.get("forge_compiler_evidence")
        diagnostics = evidence.get("diagnostics") if isinstance(evidence, dict) else None
        if (not isinstance(diagnostics, list) or not diagnostics
                or evidence.get("unparsedErrorLineDigests") != []
                or evidence.get("environmentErrorCategories") != []):
            raise ValueError("empty control lacks an isolated C# hidden-test compiler failure") from ordinary_error
        hidden_paths = _hidden_test_paths(row)
        normalized = []
        projects = set()
        for item in diagnostics:
            if (not isinstance(item, dict) or set(item) != {"path", "code", "project"}
                    or item.get("path") not in hidden_paths
                    or not isinstance(item.get("code"), str) or not re.fullmatch(r"CS\d{4}", item["code"])
                    or not isinstance(item.get("project"), str) or not item["project"].endswith(".csproj")):
                raise ValueError("compiler failure was not confined to a hidden-test source/project") from ordinary_error
            safe_path(item["project"])
            projects.add(item["project"])
            normalized.append(item)
        if len(projects) != 1:
            raise ValueError("compiler failure spans multiple or unidentified projects") from ordinary_error
        counts = value.get("forge_all_observed_status_counts")
        if (value.get("forge_pristine_build_succeeded") is not True
                or value.get("forge_independent_trx_valid") is not True
                or not isinstance(counts, dict) or not counts
                or any(not isinstance(k, str) or not isinstance(v, int) or v < 0
                                               for k, v in counts.items())
                or any(k != "PASSED" and v for k, v in counts.items())):
            raise ValueError("other tests failed while hidden tests did not compile") from ordinary_error
        signature = {"kind": "hidden-test-compile-failure", "diagnostics": normalized,
                     "project": next(iter(projects))}
        return {"kind": signature["kind"], "signatureSha256": _control_signature(signature)}
    value = report[row["instance_id"]]
    signature = {"kind": "declared-test-failure", "observed": value["forge_observed_tests"],
                 "testsStatus": value["tests_status"]}
    return {"kind": signature["kind"], "signatureSha256": _control_signature(signature)}


def validate_candidate_patch(patch_path, row, repository):
    """Reject hidden-test edits and evaluator/build configuration changes.

    This pilot intentionally tests source repairs. Infrastructure-changing tasks
    need a separately reviewed scope and cannot silently weaken the evaluator.
    """
    content = Path(patch_path).read_bytes()
    if re.search(rb"^(?:new file mode|new mode|old mode) (?:120000|160000)$", content, re.MULTILINE):
        raise ValueError("candidate symlinks/submodules are not allowed")
    result = subprocess.run(["git", "apply", "--numstat", "-z"], input=content, capture_output=True, check=True,
                            cwd=repository, env=git_env(), timeout=30)
    paths = []
    for entry in result.stdout.split(b"\0"):
        if not entry:
            continue
        pieces = entry.split(b"\t", 2)
        if len(pieces) != 3:
            raise ValueError("renames/ambiguous patch paths are unsupported in this pilot")
        name = pieces[2].decode()
        safe_path(name)
        paths.append(name)
    if not paths:
        raise ValueError("empty candidate patch")
    hidden = subprocess.run(["git", "apply", "--numstat", "-z"], input=row["test_patch"].encode(),
                            capture_output=True, check=True, cwd=repository, env=git_env(), timeout=30)
    hidden_paths = set()
    for entry in hidden.stdout.split(b"\0"):
        if entry:
            pieces = entry.split(b"\t", 2)
            if len(pieces) != 3:
                raise ValueError("ambiguous hidden test paths")
            hidden_paths.add(pieces[2].decode())
    for name in paths:
        parts = PurePosixPath(name).parts
        lower = name.lower()
        if (name in hidden_paths or any("test" in p.lower() for p in parts)
                or not lower.endswith((".cs", ".md", ".txt")) or any(p.startswith(".") for p in parts)):
            raise ValueError("pilot patch changes tests or files outside source/documentation/API-text scope")
    git(repository, "apply", "--check", str(Path(patch_path).resolve()))
    return paths


# The upstream CLI skips an empty model_patch. This inert added-file patch
# exercises base+hidden tests without modifying any source or existing files.
EMPTY_PATCH = "diff --git a/.forge-benchmark-empty-control b/.forge-benchmark-empty-control\nnew file mode 100644\n--- /dev/null\n+++ b/.forge-benchmark-empty-control\n@@ -0,0 +1 @@\n+empty-solution-control\n"


def prepared_control(path):
    prepared = Path(path).resolve()
    meta = read_json(prepared / "control/prepared.json")
    verify_source(meta["sourceRoot"])
    if digest(meta["datasetPath"]) != meta["datasetSubsetSha256"] or digest(meta["manifestPath"]) != meta["manifestSha256"]:
        raise ValueError("prepared control data changed")
    manifest = validate_manifest(meta["manifestPath"])
    raw_rows = read_json(meta["datasetPath"])
    if not isinstance(raw_rows, list) or any(not isinstance(row, dict) for row in raw_rows):
        raise ValueError("prepared dataset subset must be a list of objects")
    rows = {}
    for row in raw_rows:
        ident = row.get("instance_id")
        if not isinstance(ident, str) or ident in rows:
            raise ValueError("prepared dataset subset has a missing or duplicate instance id")
        rows[ident] = row
    if set(rows) != {case["id"] for case in manifest["cases"]}:
        raise ValueError("prepared dataset subset does not exactly match the external manifest")
    return meta, manifest, rows


def evaluate_official(meta, python, predictions, ids, output, timeout):
    """Invoke pinned official evaluator in a new directory, no production env."""
    output = Path(output).absolute()
    python = Path(python).absolute()  # Preserve the venv executable symlink.
    output.mkdir(parents=True, exist_ok=False)
    write_json(output / "predictions.json", predictions)
    env = {k: os.environ[k] for k in ("PATH", "DOCKER_HOST", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH", "XDG_RUNTIME_DIR") if k in os.environ}
    home = output / "home"
    home.mkdir()
    env.update({"HOME": str(home), "PYTHONNOUSERSITE": "1", "PYTHONDONTWRITEBYTECODE": "1",
                "PYTHONPATH": str(Path(meta["sourceRoot"]) / "harness")})
    container_label = "forge-swe-sharp-" + uuid.uuid4().hex
    env["FORGE_BENCHMARK_CONTAINER_LABEL"] = container_label
    wrapper = Path(__file__).absolute().with_name("swe_sharp_eval.py")
    command = [str(python), str(wrapper),
               "--dataset_name", meta["datasetPath"], "--predictions_path", str(output / "predictions.json"),
               "--instance_ids", *ids, "--max_workers", "1", "--run_id", "forge-" + uuid.uuid4().hex,
               "--namespace", "swebcs", "--timeout", str(timeout), "--cache_level", "instance"]
    # Reuse the driver's process-group cleanup, including descendants.
    from run import run_process
    try:
        process = run_process(command, output, env, output / "evaluator.log", timeout * len(ids) + 900)
    finally:
        cleanup = run_process(
            [str(python), str(wrapper), "--cleanup-label", container_label, str(output / "environment.json")],
            output, env, output / "cleanup.log", 120)
    process["cleanup"] = cleanup
    if cleanup["exitCode"] != 0 or cleanup["timedOut"]:
        process["cleanupFailed"] = True
        if process["exitCode"] == 0:
            process["exitCode"] = cleanup["exitCode"] or 1
    records = {}
    for path in output.glob("logs/run_evaluation/**/report.json"):
        report = read_json(path)
        if len(report) != 1:
            raise ValueError("unexpected official report shape")
        ident = next(iter(report))
        if ident in records or ident not in ids:
            raise ValueError("duplicate/unexpected official report")
        records[ident] = report
    return process, records, read_json(output / "environment.json") if (output / "environment.json").exists() else {}


def preflight(prepared, python, timeout):
    meta, manifest, rows = prepared_control(prepared)
    output = Path(prepared) / "control" / ("preflight-" + uuid.uuid4().hex[:10])
    output.mkdir()
    ids = [c["id"] for c in manifest["cases"]]
    receipt = {"schemaVersion": 1, "sourceRevision": SOURCE_REVISION, "datasetSha256": manifest["datasetSha256"],
               "manifestSha256": digest(meta["manifestPath"]), "caseIds": ids, "success": False,
               "baselines": {}, "negativeControls": {},
               "evaluatorFingerprint": None, "imageIds": {}, "errors": []}
    try:
        fingerprints = []
        image_sets = []
        compile_controls = {}
        for baseline in ("gold", "empty"):
            preds = [{"instance_id": i, "model_name_or_path": baseline,
                      "model_patch": rows[i]["patch"] if baseline == "gold" else EMPTY_PATCH} for i in ids]
            process, reports, environment = evaluate_official(meta, python, preds, ids, output / baseline, timeout)
            fingerprints.append(environment.get("fingerprint"))
            image_sets.append(environment.get("images"))
            if process["exitCode"] or process["timedOut"]:
                receipt["errors"].append(f"{baseline}: evaluator process failed; see private evaluator.log")
            for ident in ids:
                try:
                    if process["exitCode"] or process["timedOut"]:
                        raise ValueError("evaluator process failed")
                    if baseline == "gold":
                        check_official_report(reports.get(ident, {}), rows[ident], True)
                    else:
                        classification = classify_empty_baseline(reports.get(ident, {}), rows[ident])
                        receipt["negativeControls"][ident] = classification
                        if classification["kind"] == "hidden-test-compile-failure":
                            compile_controls[ident] = classification
                    receipt["baselines"].setdefault(ident, {})[baseline] = True
                except ValueError as error:
                    receipt["errors"].append(f"{baseline}/{ident}: {error}")
            if process["exitCode"] or process["timedOut"]:
                break
        if (len(fingerprints) == 2 and fingerprints[0] and fingerprints[0] == fingerprints[1]
                and image_sets[0] and image_sets[0] == image_sets[1]):
            receipt["evaluatorFingerprint"], receipt["imageIds"] = fingerprints[0], image_sets[0]
        else:
            receipt["errors"].append("evaluator/images unverified or changed between baselines")
        if not receipt["errors"] and compile_controls:
            repeat_ids = sorted(compile_controls)
            predictions = [{"instance_id": i, "model_name_or_path": "empty-repeat",
                            "model_patch": EMPTY_PATCH} for i in repeat_ids]
            process, reports, environment = evaluate_official(
                meta, python, predictions, repeat_ids, output / "empty-repeat", timeout)
            if process["exitCode"] or process["timedOut"]:
                receipt["errors"].append("empty-repeat: evaluator process failed; see private evaluator.log")
            elif (environment.get("fingerprint") != receipt["evaluatorFingerprint"]
                  or environment.get("images") != {i: receipt["imageIds"].get(i) for i in repeat_ids}):
                receipt["errors"].append("empty-repeat: evaluator/image identity changed")
            else:
                for ident in repeat_ids:
                    try:
                        repeated = classify_empty_baseline(reports.get(ident, {}), rows[ident])
                        if repeated != compile_controls[ident]:
                            raise ValueError("compiler failure signature changed on repeat")
                    except ValueError as error:
                        receipt["errors"].append(f"empty-repeat/{ident}: {error}")
        receipt["success"] = (not receipt["errors"]
            and all(receipt["baselines"].get(i) == {"gold": True, "empty": True} for i in ids)
            and set(receipt["negativeControls"]) == set(ids))
    finally:
        write_json(output / "receipt.json", receipt)
        print(f"Preflight receipt: {output / 'receipt.json'}", flush=True)
    return receipt["success"]


def evaluate(prepared, results, receipt_path, python, timeout):
    from run import external_provenance, summarize

    meta, manifest, rows = prepared_control(prepared)
    receipt = validate_preflight(meta["manifestPath"], receipt_path)
    report = read_json(results)
    if report.get("mode") != "live" or report.get("externalPreflight") != receipt:
        raise ValueError("generation report is not a live run tied to this preflight")
    provenance_manifest = dict(manifest)
    provenance_manifest["manifestSha256"] = digest(meta["manifestPath"])
    if report.get("externalDataset") != external_provenance(provenance_manifest):
        raise ValueError("generation report dataset differs")
    output = Path(prepared) / "control" / ("evaluation-" + uuid.uuid4().hex[:10])
    output.mkdir()
    evaluated = []
    processed = 0

    def save():
        write_json(output / "results.json", {"schemaVersion": 1, "generationResultsSha256": digest(results),
            "preflight": receipt, "attempts": evaluated, "notRun": report.get("notRun", []),
            "summary": summarize(evaluated), "complete": processed == len(report["attempts"])})

    save()
    try:
        for number, attempt in enumerate(report["attempts"]):
            row = dict(attempt)
            row["success"] = False
            if row.get("generationSuccess"):
                row["outcome"] = "external-evaluation-error"
            evaluated.append(row)
            save()
            if row.get("generationSuccess"):
                generated = row["result"]
                patch_path = Path(generated["patchPath"])
                if (not patch_path.is_file() or patch_path.is_symlink() or not patch_path.stat().st_size
                        or digest(patch_path) != generated.get("patchSha256")):
                    raise ValueError("exported patch is missing or changed")
                ident = row["caseId"]
                case = next((c for c in manifest["cases"] if c["id"] == ident), None)
                if case is None or generated.get("sourceBaseCommit") != case["baseCommit"]:
                    raise ValueError("generated patch base does not match prepared task")
                repository = Path(read_json(case["casePath"])["repositoryPath"])
                try:
                    validate_candidate_patch(patch_path, rows[ident], repository)
                except ValueError as error:
                    row["outcome"] = "external-candidate-rejected"
                    row["evaluationError"] = str(error)
                else:
                    predictions = [{"instance_id": ident, "model_name_or_path": "forge",
                                    "model_patch": patch_path.read_text()}]
                    process, official, environment = evaluate_official(
                        meta, python, predictions, [ident], output / str(number), timeout)
                    row["officialProcess"] = process
                    expected_images = {ident: receipt["imageIds"].get(ident)}
                    if (process["exitCode"] == 0 and not process["timedOut"]
                            and environment.get("fingerprint") == receipt["evaluatorFingerprint"]
                            and expected_images[ident]
                            and environment.get("images") == expected_images):
                        try:
                            check_official_report(official.get(ident, {}), rows[ident], True)
                            row["success"], row["outcome"] = True, "accepted"
                        except ValueError:
                            row["outcome"] = "external-tests-failed-or-incomplete"
            processed += 1
            save()
    finally:
        save()
    print(f"Official evaluation: {output}")
    return bool(evaluated) and all(r["success"] for r in evaluated) and not report.get("notRun")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    fetch = sub.add_parser("fetch")
    fetch.add_argument("--output", type=Path, default=DEFAULT_ROOT / "source" / SOURCE_REVISION)
    prep = sub.add_parser("prepare")
    prep.add_argument("--source", type=Path, default=DEFAULT_ROOT / "source" / SOURCE_REVISION)
    prep.add_argument("--selection", type=Path, default=Path(__file__).with_name("swe-sharp-pilot.json"))
    prep.add_argument("--output", type=Path, required=True)
    for name in ("preflight", "evaluate"):
        p = sub.add_parser(name)
        p.add_argument("--prepared", type=Path, required=True)
        p.add_argument("--python", type=Path, required=True, help="Isolated venv with official harness dependencies installed")
        p.add_argument("--timeout", type=int, default=1800)
        if name == "evaluate":
            p.add_argument("--results", type=Path, required=True)
            p.add_argument("--preflight", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "fetch":
        print(fetch_source(args.output))
        return 0
    if args.command == "prepare":
        output = prepare(args.source, args.selection, args.output)
        print(f"Prepared inputs: {output / 'agent/manifest.json'}")
        return 0 if read_json(output / "agent/manifest.json")["cases"] else 1
    if not 1 <= args.timeout <= 3600:
        parser.error("timeout must be between 1 and 3600 seconds")
    if args.command == "preflight":
        return 0 if preflight(args.prepared, args.python.absolute(), args.timeout) else 1
    return 0 if evaluate(args.prepared, args.results, args.preflight, args.python.absolute(), args.timeout) else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, OSError, KeyError, subprocess.SubprocessError) as error:
        print(f"SWE-Sharp setup/evaluation failed: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(2)
