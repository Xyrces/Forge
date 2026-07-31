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
    public sealed record Result(bool Ok, IReadOnlyList<string> Failures);

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
                var tail = Tail(output, 1500);
                failures.Add($"`{command}` exited {exitCode}:\n{tail}");
                logger.LogWarning("Verification({Dir}): '{Command}' exited {Code}", workDir, command, exitCode);
            }
            else
            {
                logger.LogInformation("Verification({Dir}): '{Command}' passed", workDir, command);
            }
        }
        return new Result(failures.Count == 0, failures);
    }

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
