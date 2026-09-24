"""No provider calls. Run: python3 -m unittest discover -s tools/benchmark -v."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import hashlib
import subprocess
from unittest.mock import patch as mock_patch
from decimal import Decimal

spec = importlib.util.spec_from_file_location("benchmark", Path(__file__).with_name("run.py"))
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)
swe_spec = importlib.util.spec_from_file_location("swe_sharp", Path(__file__).with_name("swe_sharp.py"))
swe_sharp = importlib.util.module_from_spec(swe_spec)
swe_spec.loader.exec_module(swe_sharp)
eval_spec = importlib.util.spec_from_file_location("swe_sharp_eval", Path(__file__).with_name("swe_sharp_eval.py"))
swe_sharp_eval = importlib.util.module_from_spec(eval_spec)
eval_spec.loader.exec_module(swe_sharp_eval)


class RunnerTests(unittest.TestCase):
    def profile(self):
        return {"id": "trial", "provider": "provider", "model": "model", "baseUrl": "https://example.com/v1",
                "apiKeyEnv": "TRIAL_API_KEY", "maxCalls": 2, "maxInputTokens": 100000,
                "maxOutputTokens": 10000, "inputUsdPerMillion": "1.5", "outputUsdPerMillion": "5"}

    @staticmethod
    def usage(calls=1, completed=1, failed=0, in_flight=0, missing=0,
              input_tokens=100, output_tokens=10, known=.0002, estimated=.0002):
        return {"accountingComplete": in_flight == 0 and missing == 0,
                "calls": calls, "completedCalls": completed, "failedCalls": failed,
                "inFlightCalls": in_flight, "missingUsageCalls": missing,
                "inputTokens": input_tokens, "outputTokens": output_tokens,
                "cachedInputTokens": 0, "cacheWriteInputTokens": 0,
                "knownUsageEstimatedUsd": known, "estimatedCostUsd": estimated}

    def test_reserves_full_attempt_including_potential_failures(self):
        self.assertEqual(Decimal("0.4"), benchmark.reservation(self.profile()))

    def test_rejects_invalid_or_secret_bearing_config(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "config.json"
            for field, value in (("maxCalls", 0), ("maxCalls", True), ("inputUsdPerMillion", "NaN"),
                                 ("id", "../escape"), ("baseUrl", "https://user:secret@example.com"),
                                 ("baseUrl", "https://example.com?key=secret"), ("apiKeyEnv", "not a variable")):
                with self.subTest(field=field, value=value):
                    profile = self.profile()
                    profile[field] = value
                    path.write_text(json.dumps({"profiles": [profile]}))
                    with self.assertRaises(ValueError):
                        benchmark.load_profiles(path)

    def test_all_failure_cost_included_in_cost_per_success(self):
        rows = [{"profile": "a", "success": True, "estimatedCostUsd": 1},
                {"profile": "a", "success": False, "estimatedCostUsd": 2}]
        result = benchmark.summarize(rows)[0]
        self.assertEqual(0.5, result["completionRate"])
        self.assertEqual(3, result["estimatedCostPerCompletedTaskUsd"])

    def test_unknown_failure_cost_invalidates_aggregate_cost(self):
        rows = [{"profile": "a", "success": True, "estimatedCostUsd": 1},
                {"profile": "a", "success": False, "estimatedCostUsd": None}]
        result = benchmark.summarize(rows)[0]
        self.assertFalse(result["costAccountingComplete"])
        self.assertIsNone(result["estimatedCostPerCompletedTaskUsd"])

    def test_malformed_and_failed_usage_stays_unknown(self):
        valid = self.usage()
        for field, value in (("inputTokens", None), ("outputTokens", -1), ("inputTokens", True),
                             ("outputTokens", "NaN"), ("failedCalls", 1), ("calls", 0),
                             ("knownUsageEstimatedUsd", float("nan")),
                             ("estimatedCostUsd", "0.0002"), ("inFlightCalls", 1),
                             ("missingUsageCalls", 1)):
            with self.subTest(field=field, value=value):
                usage = dict(valid)
                usage[field] = value
                self.assertIsNone(benchmark.estimate_cost({"usage": usage}, self.profile()))
        self.assertAlmostEqual(.0002, benchmark.estimate_cost({"usage": valid}, self.profile()))

    def test_zero_calls_with_usage_and_nonfinite_recomputed_cost_are_unknown(self):
        zero_with_tokens = self.usage(calls=0, completed=0, input_tokens=1,
                                      output_tokens=0, known=0, estimated=0)
        self.assertIsNone(benchmark.estimate_cost({"usage": zero_with_tokens}, self.profile()))
        huge = self.profile()
        huge["inputUsdPerMillion"] = "1e999999"
        self.assertIsNone(benchmark.estimate_cost({"usage": self.usage()}, huge))

    def test_zero_completions_never_looks_like_free_success(self):
        result = benchmark.summarize([{"profile": "a", "success": False, "estimatedCostUsd": 0}])[0]
        self.assertEqual(0, result["completed"])
        self.assertIsNone(result["estimatedCostPerCompletedTaskUsd"])

    def test_success_requires_matching_identity_and_real_checks(self):
        result = {"version": 1, "caseId": "calculator", "mode": "live", "provider": "provider", "model": "model",
                  "success": True, "outcome": "accepted", "checks": []}
        with self.assertRaises(ValueError):
            benchmark.validate_result(result, "calculator", "live", self.profile())
        result["success"] = False
        benchmark.validate_result(result, "calculator", "live", self.profile())
        result["model"] = "wrong-model"
        with self.assertRaises(ValueError):
            benchmark.validate_result(result, "calculator", "live", self.profile())

    def test_zero_tests_or_missing_suites_are_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "results.trx"
            path.write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results/></TestRun>')
            result = benchmark.read_trx(path)
            self.assertFalse(result["success"])
            self.assertEqual(list(benchmark.RELIABILITY_CLASSES), result["missingClasses"])

    def test_skipped_test_makes_reliability_incomplete(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "results.trx"
            rows = ''.join(f'<UnitTestResult testName="Forge.Tests.{c}.Test" outcome="Passed"/>' for c in benchmark.RELIABILITY_CLASSES)
            rows += '<UnitTestResult testName="Skipped" outcome="NotExecuted"/>'
            path.write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>' + rows + '</Results></TestRun>')
            self.assertFalse(benchmark.read_trx(path)["success"])

    @unittest.skipUnless(os.name == "posix", "process groups require POSIX")
    def test_timeout_kills_child_that_would_write_later(self):
        import time
        with tempfile.TemporaryDirectory() as temp:
            marker = Path(temp) / "escaped"
            child = "import time,pathlib; time.sleep(1); pathlib.Path(" + repr(str(marker)) + ").write_text('bad')"
            parent = "import subprocess,sys,time; subprocess.Popen([sys.executable,'-c'," + repr(child) + "]); time.sleep(10)"
            result = benchmark.run_process([sys.executable, "-c", parent], temp, dict(os.environ), Path(temp) / "log", .15)
            self.assertTrue(result["timedOut"])
            time.sleep(1.1)
            self.assertFalse(marker.exists())

    def test_environment_does_not_forward_credentials(self):
        from unittest.mock import patch
        with patch.dict(os.environ, {"GITHUB_TOKEN": "secret", "LLM_API_KEY": "secret", "FORGE_CONFIG": "production"}):
            env = benchmark.clean_env("/tmp/dotnet")
            self.assertNotIn("GITHUB_TOKEN", env)
            self.assertNotIn("LLM_API_KEY", env)
            self.assertNotIn("FORGE_CONFIG", env)


class PolicyTests(unittest.TestCase):
    profile = RunnerTests.profile
    def policy(self):
        cheap = self.profile()
        frontier = {**cheap, "id": "frontier", "provider": "other", "model": "frontier-model",
                    "apiKeyEnv": "BENCHMARK_FRONTIER_KEY", "inputUsdPerMillion": "3", "outputUsdPerMillion": "15"}
        cheap["apiKeyEnv"] = "BENCHMARK_CHEAP_KEY"
        return {"id": "mixed", "models": [cheap, frontier],
                "roles": {"engineer": "trial", "critic": "frontier", "reviewer": "frontier", "escalation": "frontier"},
                "maxEngineeringAttempts": 2}

    def test_policy_reserves_each_model_once_including_unused_escalation(self):
        self.assertEqual(Decimal("1.3"), benchmark.reservation(self.policy()))

    def test_policy_validation_rejects_unresolved_unused_or_ambiguous_models(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "config.json"
            for mutation in (lambda p: p["roles"].update(reviewer="missing"),
                             lambda p: p.update(maxEngineeringAttempts=4),
                             lambda p: p["models"].append({**self.profile(), "id": "unused"}),
                             lambda p: p["models"][1].update(apiKeyEnv=p["models"][0]["apiKeyEnv"]),
                             lambda p: p.update(secret="not-allowed")):
                policy = self.policy(); mutation(policy)
                path.write_text(json.dumps({"policies": [policy]}))
                with self.assertRaises(ValueError): benchmark.load_config(path)
            path.write_text(json.dumps({"policies": [self.policy()]}))
            self.assertEqual([self.policy()], benchmark.load_config(path))

    def test_successful_policy_result_accepts_exact_harness_check_names(self):
        names = {"dispatch", "pull request opened", "allowed file scope",
                 "trusted grader process", "simulated CI closed loop", "real reviewer approval",
                 "pushed head identity", "accepted remote head snapshot",
                 "accepted remote head acceptance"}
        result = {"version": 1, "caseId": "calculator", "mode": "live",
                  "policyId": "mixed", "success": True, "outcome": "accepted",
                  "checks": [{"name": name, "passed": True} for name in names]}

        benchmark.validate_result(result, "calculator", "live", self.policy())

    def test_policy_cost_uses_each_models_rate_and_keeps_missing_usage_unknown(self):
        policy = self.policy()
        cheap = RunnerTests.usage(input_tokens=1000, output_tokens=100, known=.002, estimated=.002)
        frontier = RunnerTests.usage(input_tokens=1000, output_tokens=100, known=.0045, estimated=.0045)
        unused = RunnerTests.usage(calls=0, completed=0, input_tokens=0, output_tokens=0,
                                   known=0, estimated=0)
        rows = {
            policy["models"][0]["id"]: {"provider": policy["models"][0]["provider"],
                                         "model": policy["models"][0]["model"], "usage": cheap},
            policy["models"][1]["id"]: {"provider": policy["models"][1]["provider"],
                                         "model": policy["models"][1]["model"], "usage": frontier},
        }
        aggregate = RunnerTests.usage(calls=2, completed=2, input_tokens=2000, output_tokens=200,
                                      known=.0065, estimated=.0065)
        self.assertAlmostEqual(.0065, benchmark.estimate_cost(
            {"usage": aggregate, "modelUsage": rows}, policy))
        rows["frontier"]["usage"] = unused
        aggregate = dict(cheap)
        self.assertAlmostEqual(.002, benchmark.estimate_cost(
            {"usage": aggregate, "modelUsage": rows}, policy))
        rows["frontier"]["usage"] = {**unused, "accountingComplete": False}
        self.assertIsNone(benchmark.estimate_cost({"usage": aggregate, "modelUsage": rows}, policy))
        del rows["frontier"]
        self.assertIsNone(benchmark.estimate_cost({"usage": aggregate, "modelUsage": rows}, policy))

    def test_policy_cost_rejects_contradictory_unused_or_aggregate_usage(self):
        policy = self.policy()
        used = RunnerTests.usage()
        unused = RunnerTests.usage(calls=0, completed=0, input_tokens=0, output_tokens=0,
                                   known=0, estimated=0)
        rows = {
            "trial": {"provider": "provider", "model": "model", "usage": used},
            "frontier": {"provider": "other", "model": "frontier-model", "usage": unused},
        }
        result = {"usage": dict(used), "modelUsage": rows}
        rows["frontier"]["usage"] = {**unused, "inFlightCalls": 1}
        self.assertIsNone(benchmark.estimate_cost(result, policy))
        rows["frontier"]["usage"] = unused
        result["usage"] = {**used, "inputTokens": used["inputTokens"] + 1}
        self.assertIsNone(benchmark.estimate_cost(result, policy))

    def test_unknown_live_accounting_invalidates_attempt_and_stops(self):
        row = {"success": True, "outcome": "accepted"}
        stop = benchmark.finalize_live_accounting(row, {"usage": None}, self.profile())
        self.assertTrue(stop)
        self.assertFalse(row["success"])
        self.assertEqual("accounting-incomplete", row["outcome"])
        self.assertIsNone(row["estimatedCostUsd"])

    def test_known_usage_policy_accounting_failure_still_stops(self):
        usage = RunnerTests.usage()
        row = {"success": False, "outcome": "acceptance-failed"}
        result = {"usage": usage, "outcome": "acceptance-failed",
                  "checks": [{"name": "policy accounting", "passed": False}]}

        self.assertTrue(benchmark.finalize_live_accounting(row, result, self.profile()))
        self.assertAlmostEqual(.0002, row["estimatedCostUsd"])

    def test_parallel_waves_overlap_but_never_overreserve(self):
        import threading
        barrier = threading.Barrier(2)
        prepared, executed, skipped, totals = [], [], [], []
        jobs = [("calculator", self.profile(), n) for n in range(4)]
        def prepare(job, amount, total):
            prepared.append(job[2]); totals.append(total); return job
        def execute(job):
            self.assertEqual([0, 1], prepared)  # Both reservations precede either call.
            executed.append(job[2]); barrier.wait(timeout=2); return {}
        benchmark.execute_waves(jobs, 2, Decimal(".8"), True, prepare, execute,
                                lambda job, result: False, lambda job, reason: skipped.append((job[2], reason)))
        self.assertCountEqual([0, 1], executed)
        self.assertEqual([Decimal(".4"), Decimal(".8")], totals)
        self.assertEqual([(2, "budget"), (3, "budget")], skipped)

    def test_provider_failure_drains_inflight_then_stops_new_wave(self):
        import threading
        barrier = threading.Barrier(2)
        executed, skipped = [], []
        jobs = [("calculator", self.profile(), n) for n in range(4)]
        def execute(job):
            executed.append(job[2]); barrier.wait(timeout=2); return {"failed": job[2] == 0}
        benchmark.execute_waves(jobs, 2, Decimal("2"), True, lambda j, a, t: j, execute,
                                lambda job, result: result["failed"], lambda job, reason: skipped.append((job[2], reason)))
        self.assertCountEqual([0, 1], executed)
        self.assertEqual([(2, "provider-or-accounting-error"), (3, "provider-or-accounting-error")], skipped)

    def test_launch_exception_is_unknown_and_cannot_launch_following_wave(self):
        skipped = []
        def execute(job): raise OSError("sensitive detail must not be saved")
        def finish(job, result):
            self.assertEqual("OSError", result["launchErrorType"])
            self.assertNotIn("sensitive", json.dumps(result)); return True
        benchmark.execute_waves([("calculator", self.profile(), n) for n in range(2)], 1, Decimal("1"), True,
                                lambda j, a, t: j, execute, finish, lambda j, r: skipped.append(j[2]))
        self.assertEqual([1], skipped)


class ExternalCaseTests(unittest.TestCase):
    def make_manifest(self, root):
        repository = root / "repository"
        repository.mkdir()
        subprocess.run(["git", "init", "-q", "-b", "main"], cwd=repository, check=True)
        subprocess.run(["git", "config", "user.name", "Benchmark"], cwd=repository, check=True)
        subprocess.run(["git", "config", "user.email", "benchmark@localhost"], cwd=repository, check=True)
        (repository / "source.cs").write_text("class Source {}\n")
        subprocess.run(["git", "add", "source.cs"], cwd=repository, check=True)
        subprocess.run(["git", "commit", "-q", "-m", "base"], cwd=repository, check=True)
        base = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip()
        tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=repository, text=True).strip()
        case = {"id": "external-1", "title": "Fix it", "prompt": "Fix source.cs",
                "repositoryPath": str(repository.resolve()), "baseCommit": base,
                "allowedPaths": ["source.cs"]}
        case_path = root / "case.json"
        case_path.write_text(json.dumps(case))
        case_hash = hashlib.sha256(case_path.read_bytes()).hexdigest()
        manifest = {"schemaVersion": 1, "dataset": "swe-sharp-bench", "sourceRevision": "a" * 40,
                    "datasetSha256": "b" * 64,
                    "cases": [{"id": "external-1", "casePath": str(case_path.resolve()),
                               "caseSha256": case_hash, "baseCommit": base,
                               "upstreamBaseCommit": "c" * 40, "repo": "owner/repo",
                               "snapshotTree": tree}]}
        manifest_path = root / "manifest.json"
        manifest_path.write_text(json.dumps(manifest))
        return manifest_path, repository, base

    def test_external_manifest_binds_case_hash_and_clean_repository(self):
        with tempfile.TemporaryDirectory() as temp:
            manifest_path, repository, _ = self.make_manifest(Path(temp))
            manifest = benchmark.load_external_cases(manifest_path)
            benchmark.verify_external_repository(manifest["cases"][0])
            provenance = benchmark.external_provenance(manifest)
            self.assertNotIn("casePath", json.dumps(provenance))
            self.assertNotIn("prompt", json.dumps(provenance))

            (repository / "source.cs").write_text("changed\n")
            with self.assertRaises(ValueError):
                benchmark.verify_external_repository(manifest["cases"][0])

    def test_external_manifest_rejects_tampered_case_and_unsafe_allowed_path(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifest_path, _, _ = self.make_manifest(root)
            manifest = json.loads(manifest_path.read_text())
            case_path = Path(manifest["cases"][0]["casePath"])
            case = json.loads(case_path.read_text())
            case["allowedPaths"] = ["../hidden-tests"]
            case_path.write_text(json.dumps(case))
            manifest["cases"][0]["caseSha256"] = hashlib.sha256(case_path.read_bytes()).hexdigest()
            manifest_path.write_text(json.dumps(manifest))
            with self.assertRaises(ValueError):
                benchmark.load_external_cases(manifest_path)

            case["allowedPaths"] = ["source.cs"]
            case_path.write_text(json.dumps(case))
            with self.assertRaises(ValueError):
                benchmark.load_external_cases(manifest_path)

    def test_external_generation_requires_bound_nonempty_patch(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifest_path, repository, base = self.make_manifest(root)
            row = benchmark.load_external_cases(manifest_path)["cases"][0]
            attempt = root / "attempt"
            attempt.mkdir()
            patch = attempt / "model.patch"
            (repository / "source.cs").write_text("class Source { public int Value => 1; }\n")
            patch.write_bytes(subprocess.check_output(["git", "diff", "--binary", "--full-index"], cwd=repository))
            subprocess.run(["git", "checkout", "--", "source.cs"], cwd=repository, check=True)
            names = {"sanitized single-commit history", "dispatch", "pull request opened",
                     "pushed head identity", "committed patch", "clean worktree",
                     "allowed external file scope", "real reviewer patch recommendation",
                     "policy model calls", "simulated CI closed loop", "remote head stable through watch",
                     "produced remote head scope", "produced remote head snapshot", "patch produced"}
            result = {"version": 1, "caseId": "external-1", "mode": "live", "policyId": "mixed",
                      "success": False, "generationSuccess": True,
                      "outcome": "pending-external-evaluation", "externalEvaluation": "pending",
                      "sourceBaseCommit": base, "producedHeadSha": "d" * 40,
                      "patchPath": str(patch.resolve()),
                      "patchSha256": hashlib.sha256(patch.read_bytes()).hexdigest(),
                      "checks": [{"name": name, "passed": True} for name in names]}
            policy = {"id": "mixed", "roles": {}, "models": []}
            benchmark.validate_external_generation_result(result, row, "live", policy, attempt)

            result["checks"][0]["passed"] = False
            with self.assertRaises(ValueError):
                benchmark.validate_external_generation_result(result, row, "live", policy, attempt)
            result["checks"][0]["passed"] = True
            result["patchSha256"] = "e" * 64
            with self.assertRaises(ValueError):
                benchmark.validate_external_generation_result(result, row, "live", policy, attempt)

    def test_failed_external_generation_retains_known_usage_accounting(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifest_path, _, base = self.make_manifest(root)
            external_row = benchmark.load_external_cases(manifest_path)["cases"][0]
            policy = PolicyTests().policy()
            used = RunnerTests.usage()
            unused = RunnerTests.usage(calls=0, completed=0, input_tokens=0, output_tokens=0,
                                       known=0, estimated=0)
            result = {"version": 1, "caseId": "external-1", "mode": "live", "policyId": "mixed",
                      "success": False, "generationSuccess": False, "outcome": "patch-generation-failed",
                      "externalEvaluation": "pending", "sourceBaseCommit": base,
                      "checks": [{"name": "dispatch", "passed": False}], "usage": used,
                      "modelUsage": {
                          "trial": {"provider": "provider", "model": "model", "usage": used},
                          "frontier": {"provider": "other", "model": "frontier-model", "usage": unused}}}
            benchmark.validate_external_generation_result(result, external_row, "live", policy, root)
            accounting_row = {"success": False, "generationSuccess": False,
                              "outcome": result["outcome"]}
            self.assertFalse(benchmark.finalize_live_accounting(accounting_row, result, policy))
            self.assertAlmostEqual(.0002, accounting_row["estimatedCostUsd"])
            self.assertEqual("patch-generation-failed", accounting_row["outcome"])

    def test_fake_external_run_reports_generation_without_claiming_evaluation_success(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifest_path, repository, base = self.make_manifest(root)
            policy = PolicyTests().policy()
            config = root / "policies.json"
            config.write_text(json.dumps({"policies": [policy]}))
            output_root = root / "output"

            def fake_process(command, cwd, env, log, timeout):
                self.assertNotIn("--benchmark-self-test-graders", command)
                if "--benchmark-self-test-external" in command:
                    log.write_text("PASS: external patch generation\n")
                    return {"exitCode": 0, "timedOut": False, "elapsedSeconds": 0.01}
                self.assertEqual(1, sum(arg.startswith("--benchmark-external-case=") for arg in command))
                self.assertFalse(any(arg.startswith("--benchmark-case=") for arg in command))
                result_path = Path(next(arg.split("=", 1)[1] for arg in command
                                        if arg.startswith("--benchmark-result=")))
                attempt = result_path.parent
                patch_path = attempt / "model.patch"
                (repository / "source.cs").write_text("class Source { public int Value => 1; }\n")
                patch_path.write_bytes(subprocess.check_output(
                    ["git", "diff", "--binary", "--full-index"], cwd=repository))
                subprocess.run(["git", "checkout", "--", "source.cs"], cwd=repository, check=True)
                result = {"version": 1, "caseId": "external-1", "mode": "fake", "policyId": "mixed",
                          "success": False, "generationSuccess": True,
                          "outcome": "pending-external-evaluation", "externalEvaluation": "pending",
                          "sourceBaseCommit": base, "producedHeadSha": "d" * 40,
                          "patchPath": str(patch_path.resolve()),
                          "patchSha256": hashlib.sha256(patch_path.read_bytes()).hexdigest(),
                          "checks": [{"name": name, "passed": True} for name in {
                              "sanitized single-commit history", "dispatch", "pull request opened",
                              "pushed head identity", "committed patch", "clean worktree",
                              "allowed external file scope", "deterministic reviewer patch recommendation",
                              "simulated CI closed loop", "remote head stable through watch",
                              "produced remote head scope", "produced remote head snapshot", "patch produced"}]}
                result_path.write_text(json.dumps(result))
                return {"exitCode": 0, "timedOut": False, "elapsedSeconds": 0.02}

            with mock_patch.object(benchmark, "run_process", side_effect=fake_process):
                exit_code = benchmark.main([
                    "--mode", "fake", "--config", str(config), "--external-cases", str(manifest_path),
                    "--output-root", str(output_root), "--no-build",
                ])

            self.assertEqual(0, exit_code)
            report_path = next(output_root.iterdir()) / "results.json"
            report = json.loads(report_path.read_text())
            self.assertEqual("external-harness", report["graderSelfTest"]["kind"])
            self.assertTrue(report["generationComplete"])
            self.assertFalse(report["success"])
            self.assertTrue(report["attempts"][0]["generationSuccess"])
            self.assertEqual("pending", report["attempts"][0]["result"]["externalEvaluation"])

    def test_external_self_test_failure_stops_before_trials(self):
        for exit_code, timed_out, message in ((1, False, "PASS: external patch generation"),
                                               (0, True, "PASS: external patch generation"),
                                               (0, False, "PASS: every trusted grader")):
            with self.subTest(exit_code=exit_code, timed_out=timed_out, message=message), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                manifest_path, _, _ = self.make_manifest(root)
                config = root / "policies.json"
                config.write_text(json.dumps({"policies": [PolicyTests().policy()]}))
                output_root = root / "output"

                def fake_process(command, cwd, env, log, timeout):
                    self.assertIn("--benchmark-self-test-external", command)
                    log.write_text(message)
                    return {"exitCode": exit_code, "timedOut": timed_out, "elapsedSeconds": 0.01}

                with mock_patch.object(benchmark, "run_process", side_effect=fake_process) as process:
                    actual = benchmark.main([
                        "--mode", "fake", "--config", str(config), "--external-cases", str(manifest_path),
                        "--output-root", str(output_root), "--no-build",
                    ])
                self.assertEqual(1, actual)
                self.assertEqual(1, process.call_count)
                report = json.loads((next(output_root.iterdir()) / "results.json").read_text())
                self.assertEqual([], report["attempts"])
                self.assertIn("no model trials started", report["setupError"])


class SweSharpEvaluationTests(unittest.TestCase):
    def evaluation_fixture(self, root):
        prepared = root / "prepared"
        control = prepared / "control"
        control.mkdir(parents=True)
        manifest_path = control / "manifest.json"
        case = {"id": "external-1", "repo": "owner/repo", "upstreamBaseCommit": "a" * 40,
                "baseCommit": "b" * 40, "snapshotTree": "c" * 40,
                "casePath": str(root / "case.json"), "caseSha256": "d" * 64}
        Path(case["casePath"]).write_text(json.dumps({"repositoryPath": str(root / "repository")}))
        manifest = {"schemaVersion": 1, "dataset": "swe-sharp-bench",
                    "sourceRevision": swe_sharp.SOURCE_REVISION, "datasetSha256": "e" * 64,
                    "cases": [case]}
        manifest_path.write_text(json.dumps(manifest))
        enriched = dict(manifest, manifestSha256=swe_sharp.digest(manifest_path))
        receipt = {"manifestSha256": enriched["manifestSha256"], "datasetSha256": "e" * 64,
                   "sourceRevision": swe_sharp.SOURCE_REVISION, "caseIds": ["external-1"],
                   "evaluatorFingerprint": "trusted-evaluator", "imageIds": {"external-1": "image@sha256:1"},
                   "receiptSha256": "f" * 64}
        patch_path = root / "candidate.patch"
        patch_path.write_text("candidate patch")
        attempt = {"caseId": "external-1", "profile": "mixed", "generationSuccess": True,
                   "success": False, "outcome": "pending-external-evaluation",
                   "result": {"patchPath": str(patch_path), "patchSha256": swe_sharp.digest(patch_path),
                              "sourceBaseCommit": "b" * 40}, "estimatedCostUsd": 1.0}
        report = {"mode": "live", "externalPreflight": receipt,
                  "externalDataset": benchmark.external_provenance(enriched), "attempts": [attempt], "notRun": []}
        results = root / "generation.json"
        results.write_text(json.dumps(report))
        meta = {"manifestPath": str(manifest_path)}
        rows = {"external-1": {"instance_id": "external-1", "FAIL_TO_PASS": ["repair"],
                               "PASS_TO_PASS": ["regression"]}}
        official = {"external-1": {"external-1": {"patch_is_None": False, "patch_exists": True,
                    "patch_successfully_applied": True, "resolved": True,
                    "forge_log_parse_success": True, "forge_independent_trx_valid": True,
                    "forge_observed_tests": {"repair": "PASSED", "regression": "PASSED"},
                    "tests_status": {"FAIL_TO_PASS": {"success": ["repair"], "failure": []},
                                     "PASS_TO_PASS": {"success": ["regression"], "failure": []}}}}}
        return prepared, results, manifest, meta, rows, receipt, official

    def test_evaluation_binds_sanitized_provenance_and_per_instance_image(self):
        with tempfile.TemporaryDirectory() as temp:
            prepared, results, manifest, meta, rows, receipt, official = self.evaluation_fixture(Path(temp))
            environment = {"fingerprint": "trusted-evaluator",
                           "images": {"external-1": "image@sha256:1"}}
            process = {"exitCode": 0, "timedOut": False}
            with mock_patch.dict(sys.modules, {"run": benchmark}), \
                    mock_patch.object(swe_sharp, "prepared_control", return_value=(meta, manifest, rows)), \
                    mock_patch.object(swe_sharp, "validate_preflight", return_value=receipt), \
                    mock_patch.object(swe_sharp, "validate_candidate_patch", return_value=["source.cs"]), \
                    mock_patch.object(swe_sharp, "evaluate_official", return_value=(process, official, environment)):
                self.assertTrue(swe_sharp.evaluate(prepared, results, Path(temp) / "receipt", Path(sys.executable), 10))
            evaluation = next((prepared / "control").glob("evaluation-*/results.json"))
            saved = json.loads(evaluation.read_text())
            self.assertTrue(saved["complete"])
            self.assertEqual("accepted", saved["attempts"][0]["outcome"])

    def test_evaluation_persists_incomplete_report_when_official_runner_raises(self):
        with tempfile.TemporaryDirectory() as temp:
            prepared, results, manifest, meta, rows, receipt, _ = self.evaluation_fixture(Path(temp))
            with mock_patch.dict(sys.modules, {"run": benchmark}), \
                    mock_patch.object(swe_sharp, "prepared_control", return_value=(meta, manifest, rows)), \
                    mock_patch.object(swe_sharp, "validate_preflight", return_value=receipt), \
                    mock_patch.object(swe_sharp, "validate_candidate_patch", return_value=["source.cs"]), \
                    mock_patch.object(swe_sharp, "evaluate_official", side_effect=RuntimeError("interrupted")):
                with self.assertRaises(RuntimeError):
                    swe_sharp.evaluate(prepared, results, Path(temp) / "receipt", Path(sys.executable), 10)
            evaluation = next((prepared / "control").glob("evaluation-*/results.json"))
            saved = json.loads(evaluation.read_text())
            self.assertFalse(saved["complete"])
            self.assertEqual("external-evaluation-error", saved["attempts"][0]["outcome"])

    def test_evaluation_rejects_test_poisoning_before_official_runner(self):
        with tempfile.TemporaryDirectory() as temp:
            prepared, results, manifest, meta, rows, receipt, _ = self.evaluation_fixture(Path(temp))
            with mock_patch.dict(sys.modules, {"run": benchmark}), \
                    mock_patch.object(swe_sharp, "prepared_control", return_value=(meta, manifest, rows)), \
                    mock_patch.object(swe_sharp, "validate_preflight", return_value=receipt), \
                    mock_patch.object(swe_sharp, "validate_candidate_patch",
                                      side_effect=ValueError("pilot patch changes tests")), \
                    mock_patch.object(swe_sharp, "evaluate_official") as official_runner:
                self.assertFalse(swe_sharp.evaluate(
                    prepared, results, Path(temp) / "receipt", Path(sys.executable), 10))
            official_runner.assert_not_called()
            evaluation = next((prepared / "control").glob("evaluation-*/results.json"))
            saved = json.loads(evaluation.read_text())
            self.assertTrue(saved["complete"])
            self.assertEqual("external-candidate-rejected", saved["attempts"][0]["outcome"])

    def test_preflight_requires_exact_baselines_and_per_case_images(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifest_path = root / "manifest.json"
            manifest = {"datasetSha256": "a" * 64,
                        "cases": [{"id": "one"}, {"id": "two"}]}
            manifest_path.write_text("{}")
            receipt = {"schemaVersion": 1, "success": True,
                       "manifestSha256": swe_sharp.digest(manifest_path), "datasetSha256": "a" * 64,
                       "sourceRevision": swe_sharp.SOURCE_REVISION, "caseIds": ["one", "two"],
                       "evaluatorFingerprint": "b" * 64,
                       "imageIds": {"one": "sha256:" + "c" * 64, "two": "sha256:" + "d" * 64},
                       "baselines": {"one": {"gold": True, "empty": True},
                                     "two": {"gold": True, "empty": True}},
                       "negativeControls": {
                           "one": {"kind": "declared-test-failure", "signatureSha256": "e" * 64},
                           "two": {"kind": "hidden-test-compile-failure", "signatureSha256": "f" * 64}},
                       "errors": []}
            receipt_path = root / "receipt.json"
            receipt_path.write_text(json.dumps(receipt))
            with mock_patch.object(swe_sharp, "validate_manifest", return_value=manifest):
                sanitized = swe_sharp.validate_preflight(manifest_path, receipt_path)
                self.assertEqual(set(receipt["imageIds"]), set(sanitized["imageIds"]))
                for mutation in (lambda value: value["imageIds"].pop("two"),
                                 lambda value: value["baselines"].update({"extra": {"gold": True, "empty": True}}),
                                 lambda value: value["errors"].append("failed")):
                    changed = json.loads(json.dumps(receipt))
                    mutation(changed)
                    receipt_path.write_text(json.dumps(changed))
                    with self.assertRaises(ValueError):
                        swe_sharp.validate_preflight(manifest_path, receipt_path)

    def test_prepared_control_rejects_duplicate_or_mismatched_subset_rows(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            control = root / "control"
            control.mkdir()
            manifest_path = control / "manifest.json"
            dataset_path = control / "dataset.json"
            manifest_path.write_text("{}")
            manifest = {"cases": [{"id": "one"}]}

            def write_prepared(rows):
                dataset_path.write_text(json.dumps(rows))
                (control / "prepared.json").write_text(json.dumps({
                    "sourceRoot": str(root / "source"), "datasetPath": str(dataset_path),
                    "manifestPath": str(manifest_path), "datasetSubsetSha256": swe_sharp.digest(dataset_path),
                    "manifestSha256": swe_sharp.digest(manifest_path)}))

            with mock_patch.object(swe_sharp, "verify_source"), \
                    mock_patch.object(swe_sharp, "validate_manifest", return_value=manifest):
                write_prepared([{"instance_id": "one"}, {"instance_id": "one"}])
                with self.assertRaisesRegex(ValueError, "duplicate"):
                    swe_sharp.prepared_control(root)
                write_prepared([{"instance_id": "one"}, {"instance_id": "extra"}])
                with self.assertRaisesRegex(ValueError, "exactly match"):
                    swe_sharp.prepared_control(root)
                write_prepared([{"instance_id": "one"}])
                _, _, rows = swe_sharp.prepared_control(root)
                self.assertEqual({"one"}, set(rows))


class SweSharpContainerCleanupTests(unittest.TestCase):
    class NotFound(Exception):
        pass

    class Container:
        def __init__(self, ident, label):
            self.id = ident
            self.labels = {swe_sharp_eval.LABEL_KEY: label}
            self.stopped = False
            self.removed = False

        def stop(self, timeout):
            self.stopped = timeout == 10

        def remove(self, force):
            self.removed = force

    class Collection:
        def __init__(self, containers):
            self.containers = containers
            self.filters = None

        def list(self, **kwargs):
            self.filters = kwargs
            return self.containers

    class Client:
        def __init__(self, containers):
            self.containers = SweSharpContainerCleanupTests.Collection(containers)

    def test_cleanup_removes_only_exactly_labeled_containers(self):
        with tempfile.TemporaryDirectory() as temp:
            label = "forge-swe-sharp-" + "a" * 32
            container = self.Container("owned", label)
            client = self.Client([container])
            environment = Path(temp) / "environment.json"
            environment.write_text(json.dumps({"containerLabel": label, "containerIds": ["owned"]}))
            swe_sharp_eval.cleanup_labeled_containers(client, label, environment, self.NotFound)
            self.assertEqual({"all": True, "filters": {"label": f"{swe_sharp_eval.LABEL_KEY}={label}"}},
                             client.containers.filters)
            self.assertTrue(container.stopped)
            self.assertTrue(container.removed)
            self.assertTrue(json.loads(environment.read_text())["cleanup"]["success"])

    def test_cleanup_refuses_label_mismatch_without_touching_container(self):
        with tempfile.TemporaryDirectory() as temp:
            label = "forge-swe-sharp-" + "a" * 32
            container = self.Container("foreign", "forge-swe-sharp-" + "b" * 32)
            environment = Path(temp) / "environment.json"
            with self.assertRaisesRegex(RuntimeError, "label mismatch"):
                swe_sharp_eval.cleanup_labeled_containers(
                    self.Client([container]), label, environment, self.NotFound)
            self.assertFalse(container.stopped)
            self.assertFalse(container.removed)
            self.assertFalse(json.loads(environment.read_text())["cleanup"]["success"])

    def test_official_timeout_still_runs_labeled_cleanup(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            calls = []

            def fake_process(command, cwd, env, log, timeout):
                calls.append((command, dict(env), timeout))
                if len(calls) == 1:
                    return {"exitCode": -9, "timedOut": True, "elapsedSeconds": 10}
                return {"exitCode": 0, "timedOut": False, "elapsedSeconds": .1}

            meta = {"sourceRoot": str(root / "source"), "datasetPath": str(root / "dataset.json")}
            with mock_patch.dict(sys.modules, {"run": benchmark}), \
                    mock_patch.object(benchmark, "run_process", side_effect=fake_process):
                process, reports, environment = swe_sharp.evaluate_official(
                    meta, Path(sys.executable), [], ["one"], root / "evaluation", 1)
            self.assertTrue(process["timedOut"])
            self.assertEqual({}, reports)
            self.assertEqual({}, environment)
            self.assertEqual(2, len(calls))
            self.assertIn("--namespace", calls[0][0])
            self.assertEqual("swebcs", calls[0][0][calls[0][0].index("--namespace") + 1])
            label = calls[0][1]["FORGE_BENCHMARK_CONTAINER_LABEL"]
            self.assertEqual(["--cleanup-label", label], calls[1][0][2:4])

    def test_cleanup_failure_invalidates_successful_official_process(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            outcomes = iter(({"exitCode": 0, "timedOut": False},
                             {"exitCode": 7, "timedOut": False}))
            meta = {"sourceRoot": str(root / "source"), "datasetPath": str(root / "dataset.json")}
            with mock_patch.dict(sys.modules, {"run": benchmark}), \
                    mock_patch.object(benchmark, "run_process", side_effect=lambda *args: next(outcomes)):
                process, _, _ = swe_sharp.evaluate_official(
                    meta, Path(sys.executable), [], ["one"], root / "evaluation", 1)
            self.assertTrue(process["cleanupFailed"])
            self.assertEqual(7, process["exitCode"])


if __name__ == "__main__":
    unittest.main()
