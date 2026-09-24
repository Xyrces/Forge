#!/usr/bin/env python3
"""Reproducible Forge engineering trials and no-LLM reliability baseline (stdlib only)."""
from __future__ import annotations

import argparse
import datetime as dt
from decimal import Decimal, InvalidOperation
import hashlib
import json
import os
from pathlib import Path
import random
import re
import shutil
import signal
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
CASES = ("calculator", "normalize", "invoice")
RELIABILITY_CLASSES = (
    "ModelRateLimitTrackerTests", "RateLimitAwareChatClientTests",
    "RunAgentExecutorTests", "RunGateTests", "QaDispatcherTests",
    "TaskStateMachineTests", "TaskStateProjectorTests", "StartupRecoveryTests",
    "KillRestartVerificationTests",
)


def write_json(path: Path, value):
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n")
    temp.replace(path)


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


def load_profiles(path: Path):
    data = json.loads(path.read_text())
    profiles = data.get("profiles", [])
    if not profiles:
        raise ValueError("config needs at least one profile")
    seen = set()
    for profile in profiles:
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


def reservation(profile):
    """Never refund reservation: failed requests can have unknown billed usage."""
    return profile["maxCalls"] * (
        profile["maxInputTokens"] * positive_decimal(profile["inputUsdPerMillion"], "input price")
        + profile["maxOutputTokens"] * positive_decimal(profile["outputUsdPerMillion"], "output price")
    ) / Decimal(1_000_000)


def run_process(command, cwd, env, log: Path, timeout):
    """Bound the entire process group, including agents' shell descendants, on POSIX."""
    started = time.monotonic()
    with log.open("w") as output:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=output,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        timed_out = False
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            code = -signal.SIGKILL
        finally:
            # Also reap descendants left behind after a normal parent exit.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
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
    usage = result.get("usage")
    if not isinstance(usage, dict) or usage.get("accountingComplete") is not True:
        return None
    for field in ("calls", "failedCalls", "inputTokens", "outputTokens"):
        value = usage.get(field)
        if isinstance(value, bool) or not isinstance(value, int) or value < 0:
            return None
    if usage["failedCalls"] or usage["calls"] == 0:
        return None
    return float((Decimal(usage["inputTokens"]) * Decimal(str(profile["inputUsdPerMillion"]))
                  + Decimal(usage["outputTokens"]) * Decimal(str(profile["outputUsdPerMillion"]))) / 1_000_000)


def validate_result(result, case, mode, profile):
    if (not isinstance(result, dict) or result.get("caseId") != case
            or result.get("mode") != mode or result.get("version") != 1
            or not isinstance(result.get("success"), bool)
            or not isinstance(result.get("outcome"), str)
            or not isinstance(result.get("checks"), list)):
        raise ValueError("invalid result identity or schema")
    if mode == "live" and (result.get("provider") != profile["provider"] or result.get("model") != profile["model"]):
        raise ValueError("result does not identify the configured provider/model")
    checks = result["checks"]
    if any(not isinstance(c, dict) or not isinstance(c.get("passed"), bool)
           or not isinstance(c.get("name"), str) for c in checks):
        raise ValueError("invalid check schema")
    required = {"dispatch", "pull request opened", "allowed file scope", "trusted grader process",
                "simulated review closed loop", "pushed head identity", "accepted remote head snapshot",
                "accepted remote head acceptance"}
    if result["success"] and (not all(c["passed"] for c in checks) or not required.issubset(c["name"] for c in checks)):
        raise ValueError("successful result lacks required passing checks")


def report_markdown(report):
    lines = ["# Forge benchmark", "", f"Mode: **{report['mode']}**. Commit: `{report['commit']}`.", "",
             "Engineering trials use simulated CI/review. Fake results establish wiring, not model quality.",
             "A missing cost is unknown, never zero; estimates price all input at the supplied upper rate, without cache discounts.",
             "These are not provider invoices. Partial matrices are not comparable policy rankings.", ""]
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("all", "fake", "reliability", "live"), default="all")
    parser.add_argument("--config", type=Path)
    parser.add_argument("--allow-live", action="store_true")
    parser.add_argument("--budget-usd", type=str)
    parser.add_argument("--cases", nargs="+", choices=CASES, default=list(CASES))
    parser.add_argument("--repetitions", type=int, default=1)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--timeout-seconds", type=int, default=300)
    parser.add_argument("--output-root", type=Path, default=ROOT / ".portHorizon" / "benchmarks")
    parser.add_argument("--dotnet", default=shutil.which("dotnet") or str(Path.home() / ".dotnet" / "dotnet"))
    parser.add_argument("--no-build", action="store_true", help="Use existing Release harness/test binaries; recorded in manifest")
    args = parser.parse_args(argv)
    if os.name != "posix":
        parser.error("POSIX required for process-group timeout cleanup; run inside a Linux worker")
    positive_int(args.repetitions, "repetitions")
    positive_int(args.timeout_seconds, "timeout-seconds")
    if args.timeout_seconds > 3600:
        parser.error("timeout-seconds must be at most 3600")
    if len(set(args.cases)) != len(args.cases):
        parser.error("cases must not repeat; use --repetitions")
    profiles = [{"id": "fake"}]
    budget = Decimal(0)
    if args.mode == "live":
        if not args.allow_live or not args.config or not args.budget_usd:
            parser.error("live requires --allow-live, --config and --budget-usd; use a disposable worker without production mounts")
        budget = positive_decimal(args.budget_usd, "budget-usd")
        profiles = load_profiles(args.config)
        for profile in profiles:
            if not os.environ.get(profile["apiKeyEnv"]):
                parser.error(f"missing credential environment variable {profile['apiKeyEnv']}")
        if min(map(reservation, profiles)) > budget:
            parser.error("budget cannot reserve even one full attempt; inspect profile token/call ceilings")
    elif args.allow_live or args.config or args.budget_usd:
        parser.error("live settings are only accepted with --mode live")
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
              "seed": args.seed, "noBuild": args.no_build, "profiles": profiles,
              "budgetUsd": str(budget), "reservedUsd": "0", "reliability": None,
              "attempts": [], "notRun": [], "summary": [], "success": False}
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
            grader_test = run_process([args.dotnet, str(binary), "--benchmark-self-test-graders",
                                      "--repo-root=" + str(output / "grader-self-test-workspace")],
                                      ROOT, env, output / "grader-self-test.log", 180)
            report["graderSelfTest"] = grader_test
            if (grader_test["exitCode"] != 0 or "PASS: every trusted grader" not in
                    (output / "grader-self-test.log").read_text(errors="replace")):
                grader_test["exitCode"] = grader_test["exitCode"] or 1
                report["setupError"] = "Independent graders failed their reference/bad-solution self-test; no model trials started."
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
            jobs = [(case, p, rep) for rep in range(1, args.repetitions + 1) for case in args.cases for p in profiles]
            random.Random(args.seed).shuffle(jobs)
            reserved = Decimal(0)
            stop_live = False
            for case, profile, rep in jobs:
                name = f"{case}-{profile['id']}-{rep}"
                reserve = reservation(profile) if args.mode == "live" else Decimal(0)
                if stop_live or reserved + reserve > budget:
                    report["notRun"].append({"attempt": name, "reason": "provider-or-accounting-error" if stop_live else "budget"})
                    save()
                    continue
                reserved += reserve
                report["reservedUsd"] = str(reserved)
                attempt = output / name
                attempt.mkdir()
                save()  # Reservation reaches disk BEFORE launching any model call.
                result_path = attempt / "result.json"
                command = [args.dotnet, str(ROOT / "tools/e2e-harness/bin/Release/net10.0/ph-e2e-harness.dll"),
                           "--repo-root=" + str(attempt / "workspace"), "--benchmark-case=" + case,
                           "--benchmark-result=" + str(result_path),
                           "--benchmark-timeout-seconds=" + str(args.timeout_seconds)]
                trial_env = dict(env)
                if args.mode == "live":
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
                row = {"profile": profile["id"], "caseId": case, "repetition": rep,
                       "success": False, "outcome": "interrupted-or-running", "estimatedCostUsd": None,
                       "reservedUsd": str(reserve), "elapsedSeconds": 0}
                report["attempts"].append(row)
                save()
                process = run_process(command, ROOT, trial_env, attempt / "run.log", args.timeout_seconds + 15)
                row.update(process)
                row["outcome"] = "timeout" if process["timedOut"] else "missing-result"
                if result_path.exists():
                    try:
                        result = json.loads(result_path.read_text())
                        expected_mode = "live" if args.mode == "live" else "fake"
                        validate_result(result, case, expected_mode, profile)
                        row["result"] = result
                        row["success"] = result["success"] and process["exitCode"] == 0
                        row["outcome"] = "timeout" if process["timedOut"] else result.get("outcome", "unknown")
                    except (ValueError, OSError):
                        row["outcome"] = "invalid-result"
                if args.mode != "live":
                    row["estimatedCostUsd"] = 0.0
                else:
                    usage = row.get("result", {}).get("usage") or {}
                    if not isinstance(usage, dict):
                        usage = {}
                    # Unknown usage (including a failed provider call) invalidates the dollar comparison.
                    row["estimatedCostUsd"] = estimate_cost(row.get("result", {}), profile)
                    # Don't spend the rest of the matrix repeatedly discovering unavailable quota/protocol.
                    if row["estimatedCostUsd"] is None or usage.get("failedCalls", 0):
                        stop_live = True
                save()
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
