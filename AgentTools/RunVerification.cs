using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Forge.AgentTools;

/// <summary>
/// Pre-push verification: runs the project's verification commands
/// (build/test gates) in the worktree and reports failures. The gate
/// exists so CI red is discovered BEFORE the branch is pushed — a
/// failed gate bounces the task back to the agent with the output,
/// no PR churn, no watch round. GitHub CI stays as the safety net.
/// </summary>
public static class RunVerification
{
    public sealed record Result(bool Ok, IReadOnlyList<string> Failures, IReadOnlyList<string>? FlakyPasses = null);

    /// <summary>Cap on isolated re-runs: a real breakage fails dozens of
    /// tests; flakiness is a handful. Also bounds the --filter length.</summary>
    internal const int MaxFlakyRetryTests = 20;

    /// <summary>Per-command timeout. Porthorizon's full xUnit suite is
    /// ~30s, Forge's ~45s; 15 minutes leaves headroom for cold restores.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    public static async Task<Result> RunAsync(
        string workDir,
        IReadOnlyList<string> commands,
        ILogger logger,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var failures = new List<string>();
        var flakyPasses = new List<string>();
        foreach (var command in commands)
        {
            var (exitCode, output, timedOut) = await RunCommandAsync(workDir, command, timeout ?? DefaultTimeout, ct);
            if (timedOut)
            {
                failures.Add($"`{command}` timed out after {timeout ?? DefaultTimeout:mm\\:ss}");
                logger.LogWarning("Verification({Dir}): '{Command}' timed out", workDir, command);
                continue;
            }
            if (exitCode != 0)
            {
                // Flaky-test self-heal (observed live 2026-08-08:
                // porthorizon task-601 stalled Failed/StalledRework
                // because the known-flaky MaterialReservationSystemTests
                // died under the full suite — a failure the task's diff
                // could not cause and no rework round could fix; the
                // bounce feedback actively misinstructs the agent).
                // A test that passes in isolation failed via full-suite
                // interference, not via the diff — quarantine it and let
                // the gate pass. Still failing in isolation = real
                // failure, bounce as before.
                if (IsDotnetTestCommand(command)
                    && ExtractFailedTests(output) is { Count: > 0 } failedTests
                    && failedTests.Count <= MaxFlakyRetryTests)
                {
                    var filter = string.Join("|", failedTests.Select(t => "FullyQualifiedName=" + t));
                    var retryCommand = command + " --filter \"" + filter + "\"";
                    logger.LogWarning(
                        "Verification({Dir}): '{Command}' failed with {Count} test failure(s) — re-running in isolation before bouncing (flaky-test quarantine)",
                        workDir, command, failedTests.Count);
                    var (retryExit, _, retryTimedOut) = await RunCommandAsync(
                        workDir, retryCommand, timeout ?? DefaultTimeout, ct);
                    if (!retryTimedOut && retryExit == 0)
                    {
                        flakyPasses.Add(
                            $"`{command}`: {failedTests.Count} test(s) failed under the full suite but PASSED in isolation (quarantined as flaky — not caused by this diff): {string.Join(", ", failedTests)}");
                        logger.LogWarning(
                            "Verification({Dir}): {Count} failed test(s) passed in isolation — quarantined as flaky, gate passes: {Tests}",
                            workDir, failedTests.Count, string.Join(", ", failedTests));
                        continue;
                    }
                    logger.LogWarning(
                        "Verification({Dir}): isolated re-run still failing — real failure, not flake", workDir);
                }
                var tail = Tail(output, 1500);
                failures.Add($"`{command}` exited {exitCode}:\n{tail}");
                logger.LogWarning("Verification({Dir}): '{Command}' exited {Code}", workDir, command, exitCode);
            }
            else
            {
                logger.LogInformation("Verification({Dir}): '{Command}' passed", workDir, command);
            }
        }
        return new Result(failures.Count == 0, failures, flakyPasses.Count > 0 ? flakyPasses : null);
    }

    private static bool IsDotnetTestCommand(string command)
    {
        // The retry command appends `--filter …`, which lands on the
        // LAST segment of a compound command — so that's the segment
        // that must be the dotnet test invocation (env-prefix forms
        // like `export PATH=…; dotnet test …` qualify).
        var last = command.Split(';')[^1].TrimStart();
        return last.StartsWith("dotnet test", StringComparison.Ordinal)
            && !last.Contains("--filter", StringComparison.Ordinal);
    }

    /// <summary>Parse fully-qualified failed test names from `dotnet test`
    /// output: xUnit console lines (`Namespace.Class.Test [FAIL]`,
    /// optionally `[xUnit.net …]`-prefixed) and VSTest summary lines
    /// (` Failed Namespace.Class.Test …`).</summary>
    internal static List<string> ExtractFailedTests(string output)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var match = XunitFailLine.Match(line);
            if (!match.Success) match = VsTestFailLine.Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            if (seen.Add(name)) names.Add(name);
        }
        return names;
    }

    private static readonly System.Text.RegularExpressions.Regex XunitFailLine = new(
        @"^\s*(?:\[xUnit\.net[^\]]*\]\s+)?(?<name>[A-Za-z_][\w.]*)\s+\[FAIL\]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex VsTestFailLine = new(
        @"^\s+Failed\s+(?<name>[A-Za-z_][\w.]*)\s*(\[|\(|$)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task<(int ExitCode, string Output, bool TimedOut)> RunCommandAsync(
        string workDir, string command, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            // Login shell: picks up the operator's PATH (~/.dotnet etc.)
            // the same way the agent's bash tool does.
            Arguments = $"-lc {Escape(command)}",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = PumpAsync(process.StandardOutput, stdout);
        var errTask = PumpAsync(process.StandardError, stderr);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return (-1, stdout.ToString(), TimedOut: true);
        }
        await Task.WhenAll(outTask, errTask);
        return (process.ExitCode, stdout + "\n" + stderr, TimedOut: false);
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder into)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            into.Append(buffer, 0, read);
            // Cap retained output — a runaway test suite can spew MBs.
            if (into.Length > 64_000) into.Remove(0, into.Length - 32_000);
        }
    }

    // .NET's argv splitting honors double quotes, not single quotes.
    private static string Escape(string command) =>
        "\"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";

    private static string Tail(string text, int max) =>
        text.Length <= max ? text.Trim() : "…" + text[^max..].Trim();

    /// <summary>Default verification when the project doesn't configure
    /// $verify: dotnet build + test for dotnet repos, nothing otherwise.</summary>
    public static IReadOnlyList<string> DefaultCommands(string workDir)
    {
        var isDotnet = Directory.EnumerateFiles(workDir, "*.sln").Any()
            || Directory.EnumerateFiles(workDir, "*.slnx").Any()
            || Directory.EnumerateFiles(workDir, "*.csproj").Any();
        return isDotnet
            ? new[] { "dotnet build -c Release --nologo", "dotnet test -c Release --nologo" }
            : Array.Empty<string>();
    }
}
