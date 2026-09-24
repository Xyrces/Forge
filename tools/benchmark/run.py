#!/usr/bin/env python3
"""Reproducible Forge engineering trials and no-LLM reliability baseline (stdlib only)."""
from __future__ import annotations

import argparse
import concurrent.futures
import datetime as dt
from decimal import Decimal, DecimalException, InvalidOperation
import hashlib
import json
import math
import os
from pathlib import Path
import random
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
CASES = ("calculator", "normalize", "invoice")
SHA1 = re.compile(r"^[0-9a-f]{40}$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
SIMPLE_ID = re.compile(r"^[A-Za-z0-9_.-]+$")
REPO_ID = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
_PROCESS_LOCK = threading.Lock()
_ACTIVE_PROCESSES = set()
_STOP_REQUESTED = threading.Event()
RELIABILITY_CLASSES = (
    "ModelRateLimitTrackerTests", "RateLimitAwareChatClientTests",
    "RunAgentExecutorTests", "RunGateTests", "QaDispatcherTests",
    "TaskStateMachineTests", "TaskStateProjectorTests", "StartupRecoveryTests",
    "KillRestartVerificationTests",
)


def write_json(path: Path, value):
    temp = path.with_suffix(path.suffix + ".tmp")
    with temp.open("w") as stream:
        stream.write(json.dumps(value, indent=2, allow_nan=False) + "\n")
        stream.flush()
        os.fsync(stream.fileno())
    temp.replace(path)
    directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def kill_process_group(process):
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass


def stop_active_processes():
    _STOP_REQUESTED.set()
    with _PROCESS_LOCK:
        processes = tuple(_ACTIVE_PROCESSES)
    for process in processes:
        kill_process_group(process)


def positive_decimal(value, name):
    try:
        number = Decimal(str(value))
    except InvalidOperation as ex:
        raise ValueError(f"{name} must be a finite positive number") from ex
    if not number.is_finite() or number <= 0:
        raise ValueError(f"{name} must be a finite positive number")
    return number


def positive_int(value, name):
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise ValueError(f"{name} must be a positive integer")
    return value


def read_json_strict(path: Path):
    def object_pairs(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise ValueError(f"duplicate JSON property: {key}")
            value[key] = item
        return value
    return json.loads(path.read_text(), object_pairs_hook=object_pairs)


def file_sha256(path: Path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_external_cases(path: Path):
    path = path.resolve()
    data = read_json_strict(path)
    required = {"schemaVersion", "dataset", "sourceRevision", "datasetSha256", "cases"}
    if not isinstance(data, dict) or set(data) != required or data.get("schemaVersion") != 1:
        raise ValueError("external case manifest schema is invalid")
    if data.get("dataset") != "swe-sharp-bench":
        raise ValueError("external case manifest dataset must be swe-sharp-bench")
    if not isinstance(data.get("sourceRevision"), str) or not SHA1.fullmatch(data["sourceRevision"]):
        raise ValueError("external sourceRevision must be a full lowercase commit SHA")
    if not isinstance(data.get("datasetSha256"), str) or not SHA256.fullmatch(data["datasetSha256"]):
        raise ValueError("external datasetSha256 must be a lowercase SHA-256")
    if not isinstance(data.get("cases"), list) or not data["cases"]:
        raise ValueError("external case manifest needs at least one case")
    seen = set()
    rows = []
    row_fields = {"id", "casePath", "caseSha256", "baseCommit", "upstreamBaseCommit", "repo", "snapshotTree"}
    case_required = {"id", "title", "prompt", "repositoryPath", "baseCommit"}
    for row in data["cases"]:
        if not isinstance(row, dict) or set(row) != row_fields:
            raise ValueError("external case rows have an invalid schema")
        case_id = row.get("id")
        if not isinstance(case_id, str) or not SIMPLE_ID.fullmatch(case_id) or case_id in seen:
            raise ValueError("external case ids must be unique simple names")
        seen.add(case_id)
        for field in ("baseCommit", "upstreamBaseCommit", "snapshotTree"):
            if not isinstance(row.get(field), str) or not SHA1.fullmatch(row[field]):
                raise ValueError(f"external case {field} must be a full lowercase Git SHA")
        if not isinstance(row.get("repo"), str) or not REPO_ID.fullmatch(row["repo"]):
            raise ValueError("external case repo must be canonical owner/name")
        if not isinstance(row.get("caseSha256"), str) or not SHA256.fullmatch(row["caseSha256"]):
            raise ValueError("external case caseSha256 must be a lowercase SHA-256")
        case_path_text = row.get("casePath")
        if not isinstance(case_path_text, str) or not Path(case_path_text).is_absolute():
            raise ValueError("external casePath must be absolute")
        case_path = Path(case_path_text).resolve()
        if not case_path.is_file() or file_sha256(case_path) != row["caseSha256"]:
            raise ValueError(f"external case file is missing or changed: {case_id}")
        case = read_json_strict(case_path)
        if (not isinstance(case, dict) or set(case) not in (case_required, case_required | {"allowedPaths"})
                or case.get("id") != case_id or case.get("baseCommit") != row["baseCommit"]):
            raise ValueError(f"external case document identity/schema mismatch: {case_id}")
        if any(not isinstance(case.get(field), str) or not case[field].strip()
               for field in ("title", "prompt")):
            raise ValueError(f"external case title/prompt must be nonempty: {case_id}")
        repository_path = case.get("repositoryPath")
        if not isinstance(repository_path, str) or not Path(repository_path).is_absolute():
            raise ValueError(f"external repositoryPath must be absolute: {case_id}")
        if "allowedPaths" in case:
            allowed = case["allowedPaths"]
            if (not isinstance(allowed, list) or not allowed
                    or any(not isinstance(item, str) or not safe_relative_path(item) for item in allowed)
                    or len(set(allowed)) != len(allowed)):
                raise ValueError(f"external allowedPaths are invalid: {case_id}")
        rows.append({**row, "casePath": str(case_path), "case": case})
    return {**data, "manifestPath": str(path), "manifestSha256": file_sha256(path), "cases": rows}


def safe_relative_path(value):
    path = Path(value)
    return (bool(value) and not path.is_absolute() and "\0" not in value and "\\" not in value
            and not any(ord(character) < 32 for character in value)
            and all(part not in ("", ".", "..") for part in path.parts))


def verify_external_repository(row):
    repository = Path(row["case"]["repositoryPath"])
    if not repository.is_dir():
        raise ValueError(f"external repository is missing: {row['id']}")
    def capture(*args):
        return subprocess.check_output(
            ["git", "-c", "core.hooksPath=/dev/null", "-C", str(repository), *args],
            text=True, stderr=subprocess.DEVNULL).strip()
    if capture("rev-parse", "HEAD") != row["baseCommit"]:
        raise ValueError(f"external repository base changed: {row['id']}")
    if capture("rev-parse", "HEAD^{tree}") != row["snapshotTree"]:
        raise ValueError(f"external repository tree changed: {row['id']}")
    if capture("status", "--porcelain", "--untracked-files=all"):
        raise ValueError(f"external repository is not clean: {row['id']}")


def external_provenance(manifest):
    return {"dataset": manifest["dataset"], "sourceRevision": manifest["sourceRevision"],
            "datasetSha256": manifest["datasetSha256"], "manifestSha256": manifest["manifestSha256"],
            "cases": [{key: row[key] for key in
                       ("id", "caseSha256", "repo", "baseCommit", "upstreamBaseCommit", "snapshotTree")}
                      for row in manifest["cases"]]}


def load_profiles(path: Path):
    data = json.loads(path.read_text())
    if set(data) != {"profiles"}:
        raise ValueError("profile config must contain only profiles")
    return validate_profiles(data["profiles"])


def validate_profiles(profiles):
    if not isinstance(profiles, list) or not profiles:
        raise ValueError("config needs at least one profile")
    seen = set()
    for profile in profiles:
        if not isinstance(profile, dict):
            raise ValueError("profile must be an object")
        required = {"id", "provider", "model", "baseUrl", "apiKeyEnv", "maxCalls",
                    "maxInputTokens", "maxOutputTokens", "inputUsdPerMillion", "outputUsdPerMillion"}
        if set(profile) != required:
            raise ValueError(f"profile fields must be exactly {sorted(required)}")
        for field in ("id", "provider", "model", "baseUrl", "apiKeyEnv"):
            if not isinstance(profile[field], str) or not profile[field].strip():
                raise ValueError(f"{field} must be a nonempty string")
        if not re.fullmatch(r"[A-Za-z0-9_-]+", profile["id"]) or profile["id"] in seen:
            raise ValueError("profile ids must be unique simple names")
        seen.add(profile["id"])
        from urllib.parse import urlsplit
        url = urlsplit(profile["baseUrl"])
        if url.scheme != "https" or not url.hostname or url.username or url.password or url.query or url.fragment:
            raise ValueError("baseUrl must be HTTPS without credentials, query, or fragment")
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", profile["apiKeyEnv"]):
            raise ValueError("apiKeyEnv must name an environment variable")
        for field in ("maxCalls", "maxInputTokens", "maxOutputTokens"):
            positive_int(profile[field], field)
        for field in ("inputUsdPerMillion", "outputUsdPerMillion"):
            positive_decimal(profile[field], field)
    return profiles


def load_config(path):
    data = json.loads(path.read_text())
    if set(data) == {"profiles"}:
        return validate_profiles(data["profiles"])
    if set(data) != {"policies"} or not isinstance(data["policies"], list) or not data["policies"]:
        raise ValueError("config must contain either profiles or nonempty policies")
    seen = set()
    for policy in data["policies"]:
        if not isinstance(policy, dict) or set(policy) != {"id", "models", "roles", "maxEngineeringAttempts"}:
            raise ValueError("policy requires id, models, roles, maxEngineeringAttempts only")
        name = policy["id"]
        if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_-]+", name) or name in seen:
            raise ValueError("policy ids must be unique simple names")
        seen.add(name)
        models = validate_profiles(policy["models"])
        ids = {m["id"] for m in models}
        roles = policy["roles"]
        if (not isinstance(roles, dict) or not {"engineer", "critic", "reviewer"}.issubset(roles)
                or set(roles) - {"engineer", "critic", "reviewer", "escalation"}
                or any(not isinstance(v, str) or v not in ids for v in roles.values())):
            raise ValueError("policy roles must resolve to declared models")
        if set(roles.values()) != ids:
            raise ValueError("policy contains unused models")
        if positive_int(policy["maxEngineeringAttempts"], "maxEngineeringAttempts") > 3:
            raise ValueError("maxEngineeringAttempts must be between 1 and 3")
        env_endpoints = {}
        for model in models:
            if not model["apiKeyEnv"].startswith("BENCHMARK_"):
                raise ValueError("policy credential variables must start with BENCHMARK_")
            from urllib.parse import urlsplit
            import ipaddress
            url = urlsplit(model["baseUrl"])
            try:
                ipaddress.ip_address(url.hostname)
                is_ip = True
            except ValueError:
                is_ip = False
            if is_ip or url.hostname.lower() == "localhost" or url.port not in (None, 443):
                raise ValueError("policy endpoints require an HTTPS hostname on port 443")
            identity = (model["provider"], model["baseUrl"])
            if model["apiKeyEnv"] in env_endpoints and env_endpoints[model["apiKeyEnv"]] != identity:
                raise ValueError("one credential variable cannot target different providers/endpoints")
            env_endpoints[model["apiKeyEnv"]] = identity
    return data["policies"]


def policy_models(profile):
    return profile["models"] if "roles" in profile else [profile]


def execute_waves(jobs, parallel, budget, live, prepare, execute, finish, skip):
    """Only the coordinator reserves/journals; launch a bounded wave after durable preparation.

    No pending executor backlog. A provider/accounting failure stops subsequent waves;
    already-authorized in-flight trials finish under their existing caps.
    """
    reserved = Decimal(0)
    stopped = False
    remaining = iter(jobs)
    exhausted = False
    with concurrent.futures.ThreadPoolExecutor(max_workers=parallel) as pool:
        try:
            while not exhausted:
                wave = []
                while len(wave) < parallel:
                    try:
                        job = next(remaining)
                    except StopIteration:
                        exhausted = True
                        break
                    amount = reservation(job[1]) if live else Decimal(0)
                    if stopped or reserved + amount > budget:
                        skip(job, "provider-or-accounting-error" if stopped else "budget")
                        continue
                    reserved += amount
                    wave.append(prepare(job, amount, reserved))
                pending = {pool.submit(execute, prepared): prepared for prepared in wave}
                for future in concurrent.futures.as_completed(pending):
                    prepared = pending[future]
                    try:
                        outcome = future.result()
                    except Exception as error:
                        # Never print exception text: subprocess setup can carry credentials.
                        outcome = {"exitCode": -1, "timedOut": False, "elapsedSeconds": 0,
                                   "launchErrorType": type(error).__name__}
                    stopped = finish(prepared, outcome) or stopped
        except BaseException:
            stop_active_processes()
            raise
    return reserved


def reservation(profile):
    """Never refund reservation: failed requests can have unknown billed usage."""
    if "roles" in profile:
        return sum((reservation(m) for m in profile["models"]), Decimal(0))
    return profile["maxCalls"] * (
        profile["maxInputTokens"] * positive_decimal(profile["inputUsdPerMillion"], "input price")
        + profile["maxOutputTokens"] * positive_decimal(profile["outputUsdPerMillion"], "output price")
    ) / Decimal(1_000_000)


def run_process(command, cwd, env, log: Path, timeout):
    """Bound the entire process group, including agents' shell descendants, on POSIX."""
    started = time.monotonic()
    if _STOP_REQUESTED.is_set():
        raise InterruptedError("Benchmark execution stopped")
    with log.open("w") as output:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=output,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        with _PROCESS_LOCK:
            _ACTIVE_PROCESSES.add(process)
        if _STOP_REQUESTED.is_set():
            kill_process_group(process)
        timed_out = False
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            code = -signal.SIGKILL
        finally:
            # Also reap descendants left behind after a normal parent exit.
            kill_process_group(process)
            process.wait()
            with _PROCESS_LOCK:
                _ACTIVE_PROCESSES.discard(process)
    return {"exitCode": code, "timedOut": timed_out,
            "elapsedSeconds": round(time.monotonic() - started, 3)}


def read_trx(path):
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    root = ET.parse(path).getroot()
    rows = [{"name": node.attrib["testName"], "outcome": node.attrib["outcome"],
             "duration": node.attrib.get("duration")}
            for node in root.findall(".//t:UnitTestResult", ns)]
    missing = [name for name in RELIABILITY_CLASSES if not any(name + "." in r["name"] for r in rows)]
    return {"success": bool(rows) and not missing and all(r["outcome"] == "Passed" for r in rows),
            "total": len(rows), "passed": sum(r["outcome"] == "Passed" for r in rows),
            "missingClasses": missing, "tests": rows}


def summarize(rows):
    summaries = []
    for profile in sorted({r["profile"] for r in rows}):
        trials = [r for r in rows if r["profile"] == profile]
        succeeded = sum(r.get("success") is True for r in trials)
        costs = [r.get("estimatedCostUsd") for r in trials]
        complete_cost = all(c is not None for c in costs)
        total = sum(costs) if complete_cost else None
        summaries.append({"profile": profile, "attempts": len(trials), "completed": succeeded,
                          "completionRate": succeeded / len(trials),
                          "estimatedCostUsd": total, "costAccountingComplete": complete_cost,
                          "estimatedCostPerCompletedTaskUsd": total / succeeded if total is not None and succeeded else None,
                          "elapsedSeconds": sum(r.get("elapsedSeconds", 0) for r in trials)})
    return summaries


def estimate_cost(result, profile):
    """Conservative text-token estimate; never invent missing or failed-call usage."""
    if "roles" in profile:
        rows = result.get("modelUsage")
        if not isinstance(rows, dict) or set(rows) != {m["id"] for m in profile["models"]}:
            return None
        aggregate = validated_usage(result.get("usage"))
        if aggregate is None:
            return None
        totals = {field: 0 for field in USAGE_INTEGER_FIELDS}
        known_estimate = Decimal(0)
        reported_estimate = Decimal(0)
        total = Decimal(0)
        calls = 0
        for model in profile["models"]:
            row = rows[model["id"]]
            if not isinstance(row, dict) or row.get("provider") != model["provider"] or row.get("model") != model["model"]:
                return None
            usage = validated_usage(row.get("usage"))
            if usage is None:
                return None
            for field in USAGE_INTEGER_FIELDS:
                totals[field] += usage[field]
            known_estimate += usage["knownUsageEstimatedUsd"]
            if usage["estimatedCostUsd"] is not None:
                reported_estimate += usage["estimatedCostUsd"]
            if usage["calls"] == 0:
                continue  # An unused escalation model is known zero, not missing usage.
            cost = estimate_cost(row, model)
            if cost is None:
                return None
            total += Decimal(str(cost))
            calls += usage["calls"]
        if any(aggregate[field] != value for field, value in totals.items()):
            return None
        if aggregate["knownUsageEstimatedUsd"] != known_estimate:
            return None
        if aggregate["estimatedCostUsd"] != reported_estimate:
            return None
        return finite_float(total) if calls else None
    usage = validated_usage(result.get("usage"))
    if usage is None or not usage["accountingComplete"]:
        return None
    if usage["failedCalls"] or usage["calls"] == 0:
        return None
    try:
        cost = ((Decimal(usage["inputTokens"]) * Decimal(str(profile["inputUsdPerMillion"])))
                + (Decimal(usage["outputTokens"]) * Decimal(str(profile["outputUsdPerMillion"])))) / 1_000_000
    except DecimalException:
        return None
    return finite_float(cost)


USAGE_INTEGER_FIELDS = (
    "calls", "completedCalls", "failedCalls", "inFlightCalls", "missingUsageCalls",
    "inputTokens", "outputTokens", "cachedInputTokens", "cacheWriteInputTokens",
)


def finite_nonnegative_decimal(value):
    if isinstance(value, bool) or not isinstance(value, (int, float, Decimal)):
        return None
    try:
        number = Decimal(str(value))
    except InvalidOperation:
        return None
    return number if number.is_finite() and number >= 0 else None


def finite_float(value):
    try:
        result = float(value)
    except (OverflowError, ValueError):
        return None
    return result if math.isfinite(result) else None


def validated_usage(value):
    if not isinstance(value, dict) or not isinstance(value.get("accountingComplete"), bool):
        return None
    usage = dict(value)
    for field in USAGE_INTEGER_FIELDS:
        item = usage.get(field)
        if isinstance(item, bool) or not isinstance(item, int) or item < 0:
            return None
    if usage["calls"] != usage["completedCalls"] + usage["failedCalls"] + usage["inFlightCalls"]:
        return None
    if usage["missingUsageCalls"] > usage["calls"]:
        return None
    complete = usage["inFlightCalls"] == 0 and usage["missingUsageCalls"] == 0
    if usage["accountingComplete"] != complete:
        return None
    if (usage["cachedInputTokens"] > usage["inputTokens"]
            or usage["cacheWriteInputTokens"] > usage["inputTokens"]):
        return None
    known = finite_nonnegative_decimal(usage.get("knownUsageEstimatedUsd"))
    estimated_raw = usage.get("estimatedCostUsd")
    estimated = None if estimated_raw is None else finite_nonnegative_decimal(estimated_raw)
    if known is None or (estimated_raw is not None and estimated is None):
        return None
    if complete != (estimated is not None):
        return None
    if estimated is not None and estimated != known:
        return None
    if usage["calls"] == 0 and (
            any(usage[field] != 0 for field in USAGE_INTEGER_FIELDS)
            or known != 0
            or estimated != 0):
        return None
    usage["knownUsageEstimatedUsd"] = known
    usage["estimatedCostUsd"] = estimated
    return usage


def finalize_live_accounting(row, result, profile):
    """Attach a trusted estimate; unknown accounting invalidates this and later trials."""
    row["estimatedCostUsd"] = estimate_cost(result, profile)
    if row["estimatedCostUsd"] is None:
        row["success"] = False
        row["outcome"] = "accounting-incomplete"
        return True
    checks = result.get("checks") if isinstance(result, dict) else None
    policy_failure = isinstance(checks, list) and any(
        isinstance(check, dict)
        and check.get("name") == "policy accounting"
        and check.get("passed") is False
        for check in checks)
    return policy_failure or result.get("outcome") in {"harness-error", "timed-out"}


def validate_result(result, case, mode, profile):
    if (not isinstance(result, dict) or result.get("caseId") != case
            or result.get("mode") != mode or result.get("version") != 1
            or not isinstance(result.get("success"), bool)
            or not isinstance(result.get("outcome"), str)
            or not isinstance(result.get("checks"), list)):
        raise ValueError("invalid result identity or schema")
    if "roles" in profile:
        if result.get("policyId") != profile["id"]:
            raise ValueError("result does not identify configured policy")
    elif mode == "live" and (result.get("provider") != profile["provider"] or result.get("model") != profile["model"]):
        raise ValueError("result does not identify the configured provider/model")
    checks = result["checks"]
    if any(not isinstance(c, dict) or not isinstance(c.get("passed"), bool)
           or not isinstance(c.get("name"), str) for c in checks):
        raise ValueError("invalid check schema")
    required = {"dispatch", "pull request opened", "allowed file scope", "trusted grader process",
                "simulated review closed loop", "pushed head identity", "accepted remote head snapshot",
                "accepted remote head acceptance"}
    if "roles" in profile:
        required.remove("simulated review closed loop")
        required.add("simulated CI closed loop")
        if mode == "live":
            required.add("real reviewer approval")
    if result["success"] and (not all(c["passed"] for c in checks) or not required.issubset(c["name"] for c in checks)):
        raise ValueError("successful result lacks required passing checks")


def validate_external_generation_result(result, row, mode, profile, attempt_dir):
    validate_result(result, row["id"], mode, profile)
    generation_success = result.get("generationSuccess")
    if (result.get("success") is not False or not isinstance(generation_success, bool)
            or result.get("externalEvaluation") != "pending"
            or result.get("sourceBaseCommit") != row["baseCommit"]):
        raise ValueError("external generation result state/identity is invalid")
    if not generation_success:
        return
    reviewer_check = ("real reviewer patch recommendation" if mode == "live"
                      else "deterministic reviewer patch recommendation")
    required_checks = {
        "sanitized single-commit history", "dispatch", "pull request opened",
        "pushed head identity", "committed patch", "clean worktree",
        "allowed external file scope", reviewer_check, "simulated CI closed loop",
        "remote head stable through watch", "produced remote head scope",
        "produced remote head snapshot", "patch produced",
    }
    if mode == "live":
        required_checks.add("policy model calls")
    checks = result["checks"]
    if (result.get("outcome") != "pending-external-evaluation"
            or not all(check["passed"] for check in checks)
            or not required_checks.issubset(check["name"] for check in checks)):
        raise ValueError("successful external generation lacks required passing checks")
    head = result.get("producedHeadSha")
    if not isinstance(head, str) or not SHA1.fullmatch(head) or head == row["baseCommit"]:
        raise ValueError("external generation result has no distinct full head SHA")
    patch_hash = result.get("patchSha256")
    patch_text = result.get("patchPath")
    if (not isinstance(patch_hash, str) or not SHA256.fullmatch(patch_hash)
            or not isinstance(patch_text, str) or not Path(patch_text).is_absolute()):
        raise ValueError("external generation patch identity is invalid")
    patch_path = Path(patch_text).resolve()
    attempt_root = Path(attempt_dir).resolve()
    try:
        patch_path.relative_to(attempt_root)
    except ValueError as ex:
        raise ValueError("external generation patch escaped its attempt directory") from ex
    if not patch_path.is_file() or patch_path.stat().st_size <= 0:
        raise ValueError("external generation patch is absent or empty")
    patch_bytes = patch_path.read_bytes()
    if not patch_bytes.strip() or hashlib.sha256(patch_bytes).hexdigest() != patch_hash:
        raise ValueError("external generation patch hash/content mismatch")
    repository = Path(row["case"]["repositoryPath"])
    apply_command = ["git", "-c", "core.hooksPath=/dev/null", "-C", str(repository),
                     "apply", "--check", "--binary", str(patch_path)]
    if subprocess.run(apply_command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode != 0:
        raise ValueError("external generation patch does not apply to its immutable base")
    changed_paths = patch_changed_paths(repository, patch_path)
    if not changed_paths:
        raise ValueError("external generation patch changes no files")
    allowed = row["case"].get("allowedPaths")
    if allowed is not None and not changed_paths.issubset(set(allowed)):
        raise ValueError("external generation patch changes files outside allowedPaths")


def patch_changed_paths(repository, patch_path):
    output = subprocess.check_output(
        ["git", "-c", "core.hooksPath=/dev/null", "-C", str(repository),
         "apply", "--numstat", "-z", "--binary", str(patch_path)],
        stderr=subprocess.DEVNULL)
    fields = output.split(b"\0")
    paths = set()
    index = 0
    while index < len(fields) and fields[index]:
        record = fields[index]
        index += 1
        pieces = record.split(b"\t", 2)
        if len(pieces) != 3:
            raise ValueError("external generation patch has malformed numstat output")
        raw_paths = []
        if pieces[2]:
            raw_paths.append(pieces[2])
        else:
            if index + 1 >= len(fields):
                raise ValueError("external generation rename metadata is incomplete")
            raw_paths.extend((fields[index], fields[index + 1]))
            index += 2
        for raw_path in raw_paths:
            try:
                value = raw_path.decode("utf-8")
            except UnicodeDecodeError as ex:
                raise ValueError("external generation patch path is not UTF-8") from ex
            if not safe_relative_path(value) or value == ".git" or value.startswith(".git/"):
                raise ValueError("external generation patch contains an unsafe path")
            paths.add(value)
    return paths


def report_markdown(report):
    lines = ["# Forge benchmark", "", f"Mode: **{report['mode']}**. Commit: `{report['commit']}`.", "",
             "CI/GitHub are simulated. Live policy trials use a real model reviewer; legacy single-model and fake trials simulate review. Fake results establish wiring only.",
             f"Parallel trial limit: {report.get('parallel', 1)}. Independent processes do not share production cooldowns or role slots.",
             "A missing cost is unknown, never zero; estimates price all input at the supplied upper rate, without cache discounts.",
             "These are not provider invoices. Partial matrices are not comparable policy rankings.", ""]
    if report.get("externalDataset"):
        complete = "YES" if report.get("generationComplete") else "NO"
        lines += [f"External patch generation complete: **{complete}**. Official SWE-Sharp evaluation is pending; "
                  "generated patches are not counted as completed tasks.", ""]
    if "trialWallSeconds" in report:
        lines += [f"Trial wall time: {report['trialWallSeconds']:.2f}s; observed concurrent trial processes: {report.get('maxObservedConcurrentTrials', 0)}.", ""]
    if report.get("reliability"):
        r = report["reliability"]
        lines += [f"Reliability baseline: **{'PASS' if r['success'] else 'FAIL'}**, {r.get('passed', 0)}/{r.get('total', 0)} passed.", ""]
    if report.get("graderSelfTest"):
        lines += [f"Independent grader self-test: **{'PASS' if report['graderSelfTest']['exitCode'] == 0 else 'FAIL'}**.", ""]
    if report.get("setupError"):
        lines += ["Setup error: " + report["setupError"], ""]
    lines += ["| Profile | Completed / attempted | Completion | Estimated USD | USD / completion |", "|---|---:|---:|---:|---:|"]
    for row in report["summary"]:
        cost = "unknown" if row["estimatedCostUsd"] is None else f"{row['estimatedCostUsd']:.6f}"
        per = "n/a" if row["estimatedCostPerCompletedTaskUsd"] is None else f"{row['estimatedCostPerCompletedTaskUsd']:.6f}"
        lines.append(f"| {row['profile']} | {row['completed']} / {row['attempts']} | {row['completionRate']:.0%} | {cost} | {per} |")
    lines += ["", f"Reserved estimate: {report['reservedUsd']} USD. {len(report['notRun'])} planned trials not run.",
              "", "See results.json for every attempt, including failures; attempt directories contain logs and usage ledgers.", ""]
    return "\n".join(lines)


def clean_env(dotnet):
    # Explicitly omit Forge config, GitHub credentials, API credentials and provider overrides.
    allowed = ("HOME", "USER", "LOGNAME", "PATH", "TMPDIR", "LANG", "LC_ALL", "DOTNET_ROOT", "NUGET_PACKAGES")
    env = {key: os.environ[key] for key in allowed if key in os.environ}
    env["PATH"] = str(Path(dotnet).parent) + os.pathsep + env.get("PATH", "")
    env.update({"DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "MSBUILDDISABLENODEREUSE": "1", "UseSharedCompilation": "false",
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
                "GIT_AUTHOR_NAME": "Forge benchmark", "GIT_AUTHOR_EMAIL": "benchmark@localhost",
                "GIT_COMMITTER_NAME": "Forge benchmark", "GIT_COMMITTER_EMAIL": "benchmark@localhost"})
    return env


def main(argv=None):
    _STOP_REQUESTED.clear()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("all", "fake", "reliability", "live"), default="all")
    parser.add_argument("--config", type=Path)
    parser.add_argument("--allow-live", action="store_true")
    parser.add_argument("--budget-usd", type=str)
    parser.add_argument("--cases", nargs="+", choices=CASES)
    parser.add_argument("--external-cases", type=Path)
    parser.add_argument("--external-preflight", type=Path)
    parser.add_argument("--repetitions", type=int, default=1)
    parser.add_argument("--parallel", type=int, default=1, help="Concurrent independent trial processes (1-8)")
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--timeout-seconds", type=int, default=300)
    parser.add_argument("--output-root", type=Path, default=ROOT / ".portHorizon" / "benchmarks")
    parser.add_argument("--dotnet", default=shutil.which("dotnet") or str(Path.home() / ".dotnet" / "dotnet"))
    parser.add_argument("--no-build", action="store_true", help="Use existing Release harness/test binaries; recorded in manifest")
    args = parser.parse_args(argv)
    if os.name != "posix":
        parser.error("POSIX required for process-group timeout cleanup; run inside a Linux worker")
    positive_int(args.repetitions, "repetitions")
    if positive_int(args.parallel, "parallel") > 8:
        parser.error("parallel must be between 1 and 8")
    positive_int(args.timeout_seconds, "timeout-seconds")
    if args.timeout_seconds > 3600:
        parser.error("timeout-seconds must be at most 3600")
    if args.external_cases and args.cases:
        parser.error("--external-cases and --cases are mutually exclusive")
    if args.external_preflight and not args.external_cases:
        parser.error("--external-preflight requires --external-cases")
    if args.external_cases and args.mode not in ("fake", "live"):
        parser.error("external cases support only fake or live mode")
    cases = args.cases or list(CASES)
    if len(set(cases)) != len(cases):
        parser.error("cases must not repeat; use --repetitions")
    external = load_external_cases(args.external_cases) if args.external_cases else None
    if external is not None:
        cases = external["cases"]
        for external_row in cases:
            verify_external_repository(external_row)
    profiles = [{"id": "fake"}]
    budget = Decimal(0)
    if args.mode == "live":
        if not args.allow_live or not args.config or not args.budget_usd:
            parser.error("live requires --allow-live, --config and --budget-usd; use a disposable worker without production mounts")
        budget = positive_decimal(args.budget_usd, "budget-usd")
        profiles = load_config(args.config)
        for profile in profiles:
            for model in policy_models(profile):
                if not os.environ.get(model["apiKeyEnv"]):
                    parser.error(f"missing credential environment variable {model['apiKeyEnv']}")
        if min(map(reservation, profiles)) > budget:
            parser.error("budget cannot reserve even one full attempt; inspect profile token/call ceilings")
    elif args.mode == "fake" and args.config and not args.allow_live and not args.budget_usd:
        profiles = load_config(args.config)
        if any("roles" not in profile for profile in profiles):
            parser.error("fake config accepts policies only")
    elif args.allow_live or args.config or args.budget_usd:
        parser.error("live settings are only accepted with --mode live")
    if external is not None and (not args.config or any("roles" not in profile for profile in profiles)):
        parser.error("external cases require mixed benchmark policies")
    preflight = None
    if external is not None and args.mode == "live":
        if not args.external_preflight:
            parser.error("live external cases require --external-preflight")
        import importlib.util
        module_path = Path(__file__).with_name("swe_sharp.py")
        module_spec = importlib.util.spec_from_file_location("forge_swe_sharp", module_path)
        if module_spec is None or module_spec.loader is None:
            raise ValueError("could not load the SWE-Sharp preflight validator")
        module = importlib.util.module_from_spec(module_spec)
        module_spec.loader.exec_module(module)
        preflight = module.validate_preflight(Path(external["manifestPath"]), args.external_preflight.resolve())
    elif external is not None and args.external_preflight:
        parser.error("fake external generation does not consume a live preflight receipt")
    # New unique child every run: never delete or reuse a caller-supplied directory.
    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex[:8]
    output = args.output_root.resolve() / run_id
    output.mkdir(parents=True, exist_ok=False)
    env = clean_env(args.dotnet)
    def git(*params):
        return subprocess.check_output(["git", *params], cwd=ROOT, text=True).strip()
    report = {"schemaVersion": 1, "mode": args.mode, "runId": run_id,
              "commit": git("rev-parse", "HEAD"), "dirty": bool(git("status", "--porcelain")),
              "trackedDiffSha256": hashlib.sha256(git("diff", "HEAD").encode()).hexdigest(),
              "seed": args.seed, "parallel": args.parallel, "noBuild": args.no_build, "profiles": profiles,
              "budgetUsd": str(budget), "reservedUsd": "0", "reliability": None,
              "attempts": [], "notRun": [], "summary": [], "success": False}
    if external is not None:
        report["externalDataset"] = external_provenance(external)
        report["externalPreflight"] = preflight
        report["generationComplete"] = False
    # Hash tooling/role sources so dirty working-tree experiments are distinguishable.
    sources = [p for folder in ("tools/e2e-harness", "tools/benchmark", "agents") for p in (ROOT / folder).rglob("*")
               if p.is_file() and not any(x in p.parts for x in ("bin", "obj", "__pycache__"))]
    report["sourceHashes"] = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(sources)}
    def save():
        report["summary"] = summarize(report["attempts"])
        write_json(output / "results.json", report)
        (output / "report.md").write_text(report_markdown(report))
    save()
    print(f"Results: {output}", flush=True)
    try:
        if args.mode != "reliability" and not args.no_build:
            build = run_process([args.dotnet, "build", "tools/e2e-harness", "-c", "Release", "--no-restore"], ROOT, env, output / "build.log", 300)
            if build["exitCode"] != 0:
                report["setupError"] = "Harness build failed; see build.log. Restore dependencies first."
                return 1
        binary = ROOT / "tools/e2e-harness/bin/Release/net10.0/ph-e2e-harness.dll"
        if args.mode != "reliability" and binary.exists():
            report["harnessBinarySha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
            # External tasks use their own SDK and independently preflighted
            # official grader. Do not require toy fixtures targeting .NET 10
            # to compile inside an otherwise valid repository-specific worker.
            self_test_flag = "--benchmark-self-test-external" if external is not None else "--benchmark-self-test-graders"
            pass_marker = "PASS: external patch generation" if external is not None else "PASS: every trusted grader"
            grader_test = run_process([args.dotnet, str(binary), self_test_flag,
                                      "--repo-root=" + str(output / "grader-self-test-workspace")],
                                      ROOT, env, output / "grader-self-test.log", 180)
            grader_test["kind"] = "external-harness" if external is not None else "fixture-graders"
            report["graderSelfTest"] = grader_test
            if (grader_test["exitCode"] != 0 or grader_test["timedOut"] or pass_marker not in
                    (output / "grader-self-test.log").read_text(errors="replace")):
                grader_test["exitCode"] = grader_test["exitCode"] or 1
                report["setupError"] = "Benchmark harness/grader self-test failed; no model trials started."
                return 1
        if args.mode in ("all", "reliability"):
            command = [args.dotnet, "test", "tests/Forge.Tests", "-c", "Release", "--no-restore",
                       "--filter", "|".join("FullyQualifiedName~" + c for c in RELIABILITY_CLASSES),
                       "--logger", "trx;LogFileName=reliability.trx", "--results-directory", str(output)]
            if args.no_build:
                command.append("--no-build")
            result = run_process(command, ROOT, env, output / "reliability.log", 600)
            trx = output / "reliability.trx"
            report["reliability"] = read_trx(trx) if trx.exists() else {"success": False, "total": 0, "passed": 0}
            report["reliability"].update(result)
            report["reliability"]["success"] &= result["exitCode"] == 0
            save()
        if args.mode != "reliability":
            jobs = [(case, p, rep) for rep in range(1, args.repetitions + 1) for case in cases for p in profiles]
            random.Random(args.seed).shuffle(jobs)
            def prepare(job, reserve, total_reserved):
                case, profile, rep = job
                case_id = case["id"] if external is not None else case
                name = f"{case_id}-{profile['id']}-{rep}"
                report["reservedUsd"] = str(total_reserved)
                attempt = output / name
                attempt.mkdir()
                save()  # Reservation reaches disk BEFORE launching any model call.
                if external is not None:
                    verify_external_repository(case)
                result_path = attempt / "result.json"
                command = [args.dotnet, str(ROOT / "tools/e2e-harness/bin/Release/net10.0/ph-e2e-harness.dll"),
                           "--repo-root=" + str(attempt / "workspace"),
                           "--benchmark-result=" + str(result_path),
                           "--benchmark-timeout-seconds=" + str(args.timeout_seconds)]
                if external is not None:
                    command.append("--benchmark-external-case=" + case["casePath"])
                else:
                    command.append("--benchmark-case=" + case_id)
                trial_env = dict(env)
                if "roles" in profile:
                    policy_path = attempt / "policy.json"
                    write_json(policy_path, profile)
                    command += ["--benchmark-policy=" + str(policy_path), "--benchmark-mode=" + args.mode]
                    if args.mode == "live":
                        command.append("--real-llm")
                        trial_env.update({m["apiKeyEnv"]: os.environ[m["apiKeyEnv"]] for m in profile["models"]})
                elif args.mode == "live":
                    command += ["--real-llm", "--benchmark-mode=live"]
                    trial_env.update({"LLM_API_KEY": os.environ[profile["apiKeyEnv"]],
                                      "LLM_BASE_URL": profile["baseUrl"], "LLM_MODEL": profile["model"],
                                      "LLM_PROVIDER": profile["provider"]})
                    command += ["--benchmark-max-calls=" + str(profile["maxCalls"]),
                                "--benchmark-max-input-tokens=" + str(profile["maxInputTokens"]),
                                "--benchmark-max-output-tokens=" + str(profile["maxOutputTokens"]),
                                "--benchmark-input-usd-per-million=" + str(profile["inputUsdPerMillion"]),
                                "--benchmark-output-usd-per-million=" + str(profile["outputUsdPerMillion"])]
                else:
                    command += ["--benchmark-mode=fake"]
                print(f"Running {name} ({args.mode})", flush=True)
                row = {"profile": profile["id"], "caseId": case_id, "repetition": rep,
                       "success": False, "outcome": "interrupted-or-running", "estimatedCostUsd": None,
                       "reservedUsd": str(reserve), "elapsedSeconds": 0,
                       "scheduledAt": dt.datetime.now(dt.timezone.utc).isoformat()}
                if external is not None:
                    row["generationSuccess"] = False
                report["attempts"].append(row)
                save()
                return (case, profile, row, command, trial_env, attempt, result_path)

            def execute(prepared):
                _, _, _, command, trial_env, attempt, _ = prepared
                started_at = dt.datetime.now(dt.timezone.utc).isoformat()
                process = run_process(command, ROOT, trial_env, attempt / "run.log", args.timeout_seconds + 15)
                return {**process, "startedAt": started_at, "finishedAt": dt.datetime.now(dt.timezone.utc).isoformat()}

            def finish(prepared, process):
                case, profile, row, _, _, _, result_path = prepared
                stop_live = False
                row.update(process)
                row["outcome"] = "timeout" if process["timedOut"] else "missing-result"
                if result_path.exists():
                    try:
                        result = read_json_strict(result_path)
                        expected_mode = "live" if args.mode == "live" else "fake"
                        if external is None:
                            validate_result(result, case, expected_mode, profile)
                        else:
                            validate_external_generation_result(
                                result, case, expected_mode, profile, result_path.parent)
                        row["result"] = result
                        if external is None:
                            row["success"] = result["success"] and process["exitCode"] == 0
                        else:
                            row["generationSuccess"] = result["generationSuccess"] and process["exitCode"] == 0
                        row["outcome"] = "timeout" if process["timedOut"] else result.get("outcome", "unknown")
                    except (ValueError, OSError):
                        row["outcome"] = "invalid-result"
                if args.mode != "live":
                    row["estimatedCostUsd"] = 0.0
                else:
                    # Unknown usage (including a failed provider call) invalidates the dollar comparison.
                    stop_live = finalize_live_accounting(row, row.get("result", {}), profile)
                    if external is not None and row["estimatedCostUsd"] is None:
                        row["generationSuccess"] = False
                    # Don't spend the rest of the matrix repeatedly discovering unavailable quota/protocol.
                save()
                return stop_live

            def skip(job, reason):
                case, profile, rep = job
                case_id = case["id"] if external is not None else case
                report["notRun"].append({"attempt": f"{case_id}-{profile['id']}-{rep}", "reason": reason})
                save()

            trials_started = time.monotonic()
            execute_waves(jobs, args.parallel, budget, args.mode == "live", prepare, execute, finish, skip)
            report["trialWallSeconds"] = round(time.monotonic() - trials_started, 3)
            events = sorted((row[field], delta) for row in report["attempts"]
                            for field, delta in (("startedAt", 1), ("finishedAt", -1)) if field in row)
            active = peak = 0
            for _, delta in events:
                active += delta
                peak = max(peak, active)
            report["maxObservedConcurrentTrials"] = peak
        if external is not None:
            report["generationComplete"] = (not report["notRun"] and bool(report["attempts"])
                                            and all(r.get("generationSuccess") is True for r in report["attempts"]))
            report["success"] = False  # Official external evaluation is a separate trusted step.
            return 0 if report["generationComplete"] else 1
        report["success"] = (not report["notRun"] and all(r["success"] for r in report["attempts"])
                             and (report["reliability"] is None or report["reliability"]["success"]))
        return 0 if report["success"] else 1
    except KeyboardInterrupt:
        report["setupError"] = "Interrupted; reserved costs are retained."
        return 130
    finally:
        save()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, OSError) as error:
        print(f"Benchmark error: {error}", file=sys.stderr)
        sys.exit(2)
