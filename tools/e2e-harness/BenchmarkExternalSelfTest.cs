using System.Text.Json;

namespace Forge.Tools.E2E;

internal static class BenchmarkExternalSelfTest
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-benchmark-external-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            Git.Run("init -q -b main", source);
            Git.Run("config user.email benchmark@local", source);
            Git.Run("config user.name forge-benchmark", source);
            await File.WriteAllTextAsync(
                Path.Combine(source, "historical-marker.txt"), "must not reach agent history", cancellationToken);
            Git.Run("add .", source);
            Git.Run("commit -q -m historical", source);
            var historicalCommit = Git.Capture("rev-parse HEAD", source).Trim();
            File.Delete(Path.Combine(source, "historical-marker.txt"));
            Directory.CreateDirectory(Path.Combine(source, "src"));
            await File.WriteAllTextAsync(
                Path.Combine(source, "src", "A.cs"), "namespace ExternalFixture; public sealed class A { }\n", cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(source, "src", "B.cs"), "namespace ExternalFixture; public sealed class B { }\n", cancellationToken);
            Git.Run("add -A", source);
            Git.Run("commit -q -m sanitized-base", source);
            var baseCommit = Git.Capture("rev-parse HEAD", source).Trim();

            var casePath = Path.Combine(root, "case.json");
            await File.WriteAllTextAsync(casePath, JsonSerializer.Serialize(new
            {
                id = "synthetic-external-case",
                title = "Produce a synthetic multi-file patch",
                prompt = "Update both C# source files.\n\nRequirements:\n\t- keep both types buildable\n\t- avoid unrelated changes\nThis wiring fixture makes no official correctness claim.",
                repositoryPath = source,
                baseCommit,
                allowedPaths = new[] { "src/A.cs", "src/B.cs" },
            }), cancellationToken);
            var loadedCase = BenchmarkExternalCase.Load(casePath);
            if (!loadedCase.Prompt.Contains('\n') || !loadedCase.Prompt.Contains('\t'))
                return Fail("multiline-prompt", "formatting whitespace was not preserved");
            var policyPath = Path.Combine(root, "policy.json");
            await File.WriteAllTextAsync(policyPath, PolicyJson, cancellationToken);

            foreach (var scenario in new[] { "multi-file", "no-change", "review-reject" })
            {
                var scenarioRoot = Path.Combine(root, scenario);
                Directory.CreateDirectory(scenarioRoot);
                var resultPath = Path.Combine(scenarioRoot, "result.json");
                var exitCode = await BenchmarkHarness.RunAsync(
                [
                    $"--repo-root={Path.Combine(scenarioRoot, "attempt")}",
                    $"--benchmark-result={resultPath}",
                    $"--benchmark-external-case={casePath}",
                    $"--benchmark-policy={policyPath}",
                    "--benchmark-mode=fake",
                    "--benchmark-timeout-seconds=120",
                    $"--benchmark-external-self-test-scenario={scenario}",
                ]);
                if (!File.Exists(resultPath)) return Fail(scenario, "result was not written");
                using var document = JsonDocument.Parse(
                    await File.ReadAllTextAsync(resultPath, cancellationToken));
                if (!Validate(scenario, exitCode, document.RootElement, baseCommit, out var error))
                    return Fail(scenario, error);
                var importedClone = Path.Combine(scenarioRoot, "attempt", ".portHorizon", "e2e", "clone");
                var visibleHistory = Git.Capture("rev-list --all", importedClone);
                if (visibleHistory.Contains(historicalCommit, StringComparison.Ordinal))
                    return Fail(scenario, "source repository history leaked into the agent clone");
                if (GitObjectExists(importedClone, historicalCommit))
                    return Fail(scenario, "an unreachable source-history object remained in the agent clone");
                Console.WriteLine($"external self-test: {scenario} PASS");
            }
            Console.WriteLine("PASS: external patch generation, no-change, review rejection, and history isolation behaved as expected.");
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
        string baseCommit,
        out string error)
    {
        var generationSuccess = result.GetProperty("generationSuccess").GetBoolean();
        var success = result.GetProperty("success").GetBoolean();
        var checks = result.GetProperty("checks").EnumerateArray().ToArray();
        var hasAcceptanceClaim = checks.Any(check =>
            check.GetProperty("name").GetString()?.Contains("acceptance", StringComparison.OrdinalIgnoreCase) == true);
        var patchPath = result.TryGetProperty("patchPath", out var patchElement)
            && patchElement.ValueKind == JsonValueKind.String
                ? patchElement.GetString()
                : null;
        var valid = scenario switch
        {
            "multi-file" => exitCode == 0 && generationSuccess && !success && !hasAcceptanceClaim
                && result.GetProperty("outcome").GetString() == "pending-external-evaluation"
                && result.GetProperty("externalEvaluation").GetString() == "pending"
                && result.GetProperty("sourceBaseCommit").GetString() == baseCommit
                && result.GetProperty("patchSha256").GetString()?.Length == 64
                && patchPath is not null && File.Exists(patchPath)
                && File.ReadAllText(patchPath).Contains("src/A.cs", StringComparison.Ordinal)
                && File.ReadAllText(patchPath).Contains("src/B.cs", StringComparison.Ordinal),
            "no-change" => exitCode != 0 && !generationSuccess && !success && patchPath is null,
            "review-reject" => exitCode != 0 && !generationSuccess && !success && patchPath is null
                && result.GetProperty("reviewVerdict").GetString() == "changes-requested",
            _ => false,
        };
        error = valid ? "" : $"unexpected external result (exit={exitCode}, generationSuccess={generationSuccess}, "
            + $"outcome={result.GetProperty("outcome").GetString()}, error="
            + $"{(result.TryGetProperty("error", out var resultError) ? resultError.ToString() : "<none>")}, checks="
            + string.Join("; ", checks.Select(check =>
                $"{check.GetProperty("name").GetString()}={check.GetProperty("passed").GetBoolean()}:"
                + check.GetProperty("detail").GetString()))
            + ", attempts=" + string.Join("; ", result.GetProperty("attempts").EnumerateArray().Select(attempt =>
                $"{attempt.GetProperty("stage").GetString()}:{attempt.GetProperty("reason").GetString()}"));
        return valid;
    }

    private static int Fail(string scenario, string error)
    {
        Console.Error.WriteLine($"External benchmark self-test failed for {scenario}: {error}");
        return 1;
    }

    private static bool GitObjectExists(string repository, string objectId)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("cat-file");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(objectId);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Could not inspect external self-test git objects.");
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private const string PolicyJson = """
        {
          "id": "external-selftest-policy",
          "models": [
            {
              "id": "engineer", "provider": "fake", "model": "engineer-model",
              "baseUrl": "https://example.com/v1", "apiKeyEnv": "BENCHMARK_EXTERNAL_ENGINEER_KEY",
              "maxCalls": 4, "maxInputTokens": 100000, "maxOutputTokens": 8000,
              "inputUsdPerMillion": 1, "outputUsdPerMillion": 1
            },
            {
              "id": "critic", "provider": "fake", "model": "critic-model",
              "baseUrl": "https://example.com/v1", "apiKeyEnv": "BENCHMARK_EXTERNAL_CRITIC_KEY",
              "maxCalls": 4, "maxInputTokens": 100000, "maxOutputTokens": 8000,
              "inputUsdPerMillion": 1, "outputUsdPerMillion": 1
            },
            {
              "id": "reviewer", "provider": "fake", "model": "reviewer-model",
              "baseUrl": "https://example.com/v1", "apiKeyEnv": "BENCHMARK_EXTERNAL_REVIEWER_KEY",
              "maxCalls": 4, "maxInputTokens": 100000, "maxOutputTokens": 8000,
              "inputUsdPerMillion": 1, "outputUsdPerMillion": 1
            }
          ],
          "roles": { "engineer": "engineer", "critic": "critic", "reviewer": "reviewer" },
          "maxEngineeringAttempts": 2
        }
        """;
}
