"""No provider calls. Run: python3 -m unittest discover -s tools/benchmark -v."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from decimal import Decimal

spec = importlib.util.spec_from_file_location("benchmark", Path(__file__).with_name("run.py"))
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


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


if __name__ == "__main__":
    unittest.main()
