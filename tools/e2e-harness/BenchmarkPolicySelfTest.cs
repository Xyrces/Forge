using System.Text.Json;

namespace Forge.Tools.E2E;

internal static class BenchmarkPolicySelfTest
{
    private static readonly string[] Scenarios =
    [
        "no-progress",
        "review-rework",
        "malformed-review",
        "grader-reject",
        "provider-failure",
    ];

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (!ValidatePlanCriticAudit(out var auditError))
            return Fail("plan-critic-audit", auditError);
        Console.WriteLine("policy self-test: plan-critic-audit PASS");

        var root = Path.Combine(Path.GetTempPath(), $"forge-benchmark-policy-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var policyPath = Path.Combine(root, "policy.json");
            await File.WriteAllTextAsync(policyPath, PolicyJson, cancellationToken);
            foreach (var scenario in Scenarios)
            {
                var scenarioRoot = Path.Combine(root, scenario);
                var resultPath = Path.Combine(scenarioRoot, "result.json");
                Directory.CreateDirectory(scenarioRoot);
                var exitCode = await BenchmarkHarness.RunAsync(
                [
                    $"--repo-root={Path.Combine(scenarioRoot, "workspace")}",
                    "--benchmark-case=calculator",
                    $"--benchmark-result={resultPath}",
                    "--benchmark-mode=fake",
                    "--benchmark-timeout-seconds=120",
                    $"--benchmark-policy={policyPath}",
                    $"--benchmark-policy-self-test-scenario={scenario}",
                ]);
                if (!File.Exists(resultPath))
                    return Fail(scenario, "result was not written");
                using var document = JsonDocument.Parse(
                    await File.ReadAllTextAsync(resultPath, cancellationToken));
                if (!Validate(scenario, exitCode, document.RootElement, out var error))
                    return Fail(scenario, error);
                Console.WriteLine($"policy self-test: {scenario} PASS");
            }
            Console.WriteLine("PASS: benchmark policy rework, escalation, review, grader, and provider-stop paths behaved as expected.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* best-effort cleanup of harness-owned temporary state */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup of harness-owned temporary state */ }
        }
    }

    private static bool Validate(
        string scenario,
        int exitCode,
        JsonElement result,
        out string error)
    {
        var success = result.GetProperty("success").GetBoolean();
        var escalated = result.GetProperty("escalated").GetBoolean();
        var attempts = result.GetProperty("attempts").EnumerateArray().ToArray();
        var engineering = attempts.Where(a => a.GetProperty("stage").GetString() == "engineering").ToArray();
        var reviews = attempts.Where(a => a.GetProperty("stage").GetString() == "final-review").ToArray();
        var graders = attempts.Where(a => a.GetProperty("stage").GetString() == "grader").ToArray();
        var checks = result.GetProperty("checks").EnumerateArray().ToArray();
        bool HasCheck(string name, bool passed) => checks.Any(c =>
            c.GetProperty("name").GetString() == name && c.GetProperty("passed").GetBoolean() == passed);

        var valid = scenario switch
        {
            "no-progress" => exitCode == 0 && success && escalated
                && engineering.Length == 2
                && !engineering[0].GetProperty("success").GetBoolean()
                && engineering[1].GetProperty("success").GetBoolean(),
            "review-rework" => exitCode == 0 && success && escalated
                && engineering.Length == 2 && reviews.Length == 2
                && reviews[0].GetProperty("reviewVerdict").GetString() == "changes-requested"
                && reviews[1].GetProperty("reviewVerdict").GetString() == "approve"
                && engineering[0].GetProperty("headSha").GetString()
                    != engineering[1].GetProperty("headSha").GetString(),
            "malformed-review" => exitCode != 0 && !success && engineering.Length == 1
                && reviews.Length == 1 && reviews[0].GetProperty("reviewVerdict").GetString() == "error"
                && !HasCheck("simulated CI closed loop", true),
            "grader-reject" => exitCode != 0 && !success && engineering.Length == 2
                && reviews.Length == 0
                && attempts.Where(a => a.GetProperty("stage").GetString() == "grader")
                    .All(a => !a.GetProperty("success").GetBoolean()),
            "provider-failure" => exitCode != 0 && !success && !escalated
                && engineering.Length == 1 && reviews.Length == 0 && graders.Length == 0
                && HasCheck("policy accounting", false)
                && result.GetProperty("usage").GetProperty("calls").GetInt32() == 1
                && result.GetProperty("usage").GetProperty("failedCalls").GetInt32() == 1
                && result.GetProperty("modelUsage").EnumerateObject()
                    .Sum(model => model.Value.GetProperty("usage").GetProperty("failedCalls").GetInt32()) == 1
                && result.GetProperty("modelUsage").GetProperty("engineer")
                    .GetProperty("usage").GetProperty("failedCalls").GetInt32() == 1
                && result.GetProperty("modelUsage").GetProperty("escalation")
                    .GetProperty("usage").GetProperty("calls").GetInt32() == 0
                && !HasCheck("accepted remote head acceptance", true),
            _ => false,
        };
        error = valid ? "" : $"unexpected result (exit={exitCode}, success={success}, attempts={attempts.Length})";
        return valid;
    }

    private static int Fail(string scenario, string error)
    {
        Console.Error.WriteLine($"Policy self-test failed for {scenario}: {error}");
        return 1;
    }

    private static bool ValidatePlanCriticAudit(out string error)
    {
        var cases = new[]
        {
            (Json: (string?)null, Success: (bool?)null, Outcome: "not-observed", Detail: "skipped"),
            (Json: GateAudit("Approve", "approved"), Success: (bool?)true, Outcome: "approve", Detail: "approved"),
            (Json: GateAudit("Revise", "change the tests"), Success: (bool?)false, Outcome: "revise", Detail: "change the tests"),
            (Json: GateAudit("Approve", "critic unavailable (approved with warning): TimeoutException"),
                Success: (bool?)null, Outcome: "approve-with-warning", Detail: "approved with warning"),
            (Json: "{\"verdicts\":[{\"gate\":\"plan-llm-review\",\"outcome\":17,\"feedback\":{}}]}",
                Success: (bool?)null, Outcome: "unknown", Detail: "without feedback"),
        };
        foreach (var item in cases)
        {
            var actual = BenchmarkHarness.ParsePlanCriticAudit(item.Json);
            if (actual.Success != item.Success
                || actual.Outcome != item.Outcome
                || !actual.Detail.Contains(item.Detail, StringComparison.OrdinalIgnoreCase))
            {
                error = $"expected {item.Outcome}/{item.Success}, got {actual.Outcome}/{actual.Success}: {actual.Detail}";
                return false;
            }
        }
        error = "";
        return true;
    }

    private static string GateAudit(string outcome, string feedback) => JsonSerializer.Serialize(new
    {
        failed = outcome == "Revise",
        verdicts = new[]
        {
            new { gate = "plan-schema", outcome = "Approve", feedback = "approved" },
            new { gate = "plan-llm-review", outcome, feedback },
        },
    });

    private const string PolicyJson = """
        {
          "id": "selftest-policy",
          "models": [
            {
              "id": "engineer", "provider": "fake", "model": "engineer-model",
              "baseUrl": "https://example.com/v1", "apiKeyEnv": "BENCHMARK_SELFTEST_ENGINEER_KEY",
              "maxCalls": 20, "maxInputTokens": 100000, "maxOutputTokens": 8000,
              "inputUsdPerMillion": 1, "outputUsdPerMillion": 1
            },
            {
              "id": "critic", "provider": "fake", "model": "critic-model",
              "baseUrl": "https://example.com/v1", "apiKeyEnv": "BENCHMARK_SELFTEST_CRITIC_KEY",
              "maxCalls": 20, "maxInputTokens": 100000, "maxOutputTokens": 8000,
              "inputUsdPerMillion": 1, "outputUsdPerMillion": 1
            },
            {
              "id": "reviewer", "provider": "fake", "model": "reviewer-model",
              "baseUrl": "https://example.com/v1", "apiKeyEnv": "BENCHMARK_SELFTEST_REVIEWER_KEY",
              "maxCalls": 20, "maxInputTokens": 100000, "maxOutputTokens": 8000,
              "inputUsdPerMillion": 1, "outputUsdPerMillion": 1
            },
            {
              "id": "escalation", "provider": "fake", "model": "escalation-model",
              "baseUrl": "https://example.com/v1", "apiKeyEnv": "BENCHMARK_SELFTEST_ESCALATION_KEY",
              "maxCalls": 20, "maxInputTokens": 100000, "maxOutputTokens": 8000,
              "inputUsdPerMillion": 1, "outputUsdPerMillion": 1
            }
          ],
          "roles": {
            "engineer": "engineer",
            "critic": "critic",
            "reviewer": "reviewer",
            "escalation": "escalation"
          },
          "maxEngineeringAttempts": 2
        }
        """;
}
