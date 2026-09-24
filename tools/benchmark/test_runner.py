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
        valid = {"accountingComplete": True, "calls": 1, "failedCalls": 0, "inputTokens": 100, "outputTokens": 10}
        for field, value in (("inputTokens", None), ("outputTokens", -1), ("inputTokens", True),
                             ("outputTokens", "NaN"), ("failedCalls", 1), ("calls", 0)):
            with self.subTest(field=field, value=value):
                usage = dict(valid)
                usage[field] = value
                self.assertIsNone(benchmark.estimate_cost({"usage": usage}, self.profile()))
        self.assertAlmostEqual(.0002, benchmark.estimate_cost({"usage": valid}, self.profile()))

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


if __name__ == "__main__":
    unittest.main()
