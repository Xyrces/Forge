using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Core;
using Microsoft.Extensions.Logging;

namespace Forge.AgentTools;

public static class WorkerVerification
{
    public static string CommandsHash(IReadOnlyList<string> commands) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(commands))).ToLowerInvariant();

    public static async Task<VerificationReceipt> VerifyAsync(string attemptId, string worktree,
        IReadOnlyList<string> commands, ILogger logger, CancellationToken ct)
    {
        var before = await TreeAsync(worktree, ct);
        var result = await RunVerification.RunAsync(worktree, commands, logger, ct);
        if (!result.Ok) throw new InvalidOperationException("Worker verification failed: " + string.Join("\n", result.Failures));
        var after = await TreeAsync(worktree, ct);
        if (before != after) throw new InvalidOperationException("Verification changed source files; rerun against stable source before publication.");
        return new(attemptId, after, CommandsHash(commands), DateTimeOffset.UtcNow);
    }

    public static async Task<bool> MatchesAsync(VerificationReceipt receipt, string worktree,
        IReadOnlyList<string> commands, CancellationToken ct) =>
        receipt.CommandsSha == CommandsHash(commands) && receipt.TreeSha == await TreeAsync(worktree, ct);

    /// <summary>Prospective commit tree using a private index; tracked and untracked nonignored edits are included.</summary>
    public static async Task<string> TreeAsync(string worktree, CancellationToken ct)
    {
        var index = Path.Combine(Path.GetTempPath(), "forge-verification-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            await GitAsync(worktree, index, ["read-tree", "HEAD"], ct);
            await GitAsync(worktree, index, ["add", "-A", "--", "."], ct);
            return (await GitAsync(worktree, index, ["write-tree"], ct)).Trim();
        }
        finally
        {
            File.Delete(index);
            File.Delete(index + ".lock");
        }
    }

    private static async Task<string> GitAsync(string worktree, string index, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = worktree, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.Environment["GIT_INDEX_FILE"] = index;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git verification snapshot.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Git verification snapshot failed: " + await error);
        return await output;
    }
}
