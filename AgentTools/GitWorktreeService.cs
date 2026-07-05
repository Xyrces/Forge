using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.AgentTools;

public sealed class GitWorktreeService
{
    private readonly WorkspaceOptions _options;
    private readonly ILogger<GitWorktreeService> _logger;

    public GitWorktreeService(WorkspaceOptions options, ILogger<GitWorktreeService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string WorkspaceRoot => Path.GetFullPath(_options.Root);
    public string WorktreeRoot => Path.GetFullPath(Path.Combine(_options.Root, _options.WorktreeRoot));
    public string DefaultBranch => _options.DefaultBranch;

    public string WorktreePathFor(string taskId)
        => Path.Combine(WorktreeRoot, Sanitize(taskId));

    public async Task<string> CreateAsync(string taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        var branch = $"agent/{Sanitize(taskId)}";
        var worktreePath = WorktreePathFor(taskId);

        Directory.CreateDirectory(WorktreeRoot);

        if (Directory.Exists(worktreePath))
        {
            _logger.LogInformation("Worktree already exists for {TaskId} at {Path}; reusing", taskId, worktreePath);
            return worktreePath;
        }

        var args = $"worktree add -B \"{branch}\" \"{worktreePath}\" \"{baseBranch}\"";
        var result = await RunGitAsync(args, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git worktree add failed (exit={result.ExitCode}): {result.Stderr}");

        _logger.LogInformation("Created worktree {Path} on branch {Branch} from {Base}", worktreePath, branch, baseBranch);
        return worktreePath;
    }

    public async Task RemoveAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var worktreePath = WorktreePathFor(taskId);
        if (!Directory.Exists(worktreePath))
        {
            _logger.LogDebug("Worktree for {TaskId} does not exist; skipping", taskId);
            return;
        }
        var args = $"worktree remove --force \"{worktreePath}\"";
        var result = await RunGitAsync(args, cancellationToken);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("git worktree remove failed (exit={Code}); falling back to directory delete: {Err}",
                result.ExitCode, result.Stderr);
            try { Directory.Delete(worktreePath, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Manual delete of worktree path failed"); }
        }
        await RunGitAsync("worktree prune", cancellationToken);
    }

    public async Task<DiffStats> GetDiffStatsAsync(string worktreePath, string baseBranch, CancellationToken cancellationToken = default)
    {
        var result = await RunGitInAsync(worktreePath, $"diff --stat \"{baseBranch}...HEAD\"", cancellationToken);
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var summary = lines.Length == 0 ? string.Empty : lines[^1];
        return new DiffStats(result.Stdout, summary);
    }

    public async Task<CommitResult> CommitAllAsync(string worktreePath, string message, CancellationToken cancellationToken = default)
    {
        await RunGitInAsync(worktreePath, "add -A", cancellationToken);
        var msgPath = Path.Combine(Path.GetTempPath(), $"commit-msg-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(msgPath, message, cancellationToken);
        try
        {
            var result = await RunGitInAsync(worktreePath, $"commit -F \"{msgPath}\"", cancellationToken);
            if (result.ExitCode == 0)
                return CommitResult.Created(result.Stdout.Trim());

            // "nothing to commit" is a normal no-op, not an error.
            if (result.Stderr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
                || result.Stdout.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                return CommitResult.NoChanges(result.Stdout.Trim());

            throw new InvalidOperationException($"git commit failed (exit={result.ExitCode}): {result.Stderr}");
        }
        finally
        {
            try { File.Delete(msgPath); } catch { }
        }
    }

    public async Task<string> PushAsync(string worktreePath, string branch, CancellationToken cancellationToken = default)
    {
        var result = await RunGitInAsync(worktreePath, $"push -u origin \"{branch}\"", cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git push failed (exit={result.ExitCode}): {result.Stderr}");
        return result.Stdout.Trim();
    }

    public async Task<string> GetHeadShaAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        var result = await RunGitInAsync(worktreePath, "rev-parse HEAD", cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git rev-parse HEAD failed: {result.Stderr}");
        return result.Stdout.Trim();
    }

    public async Task<bool> WorktreeExistsAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(worktreePath)) return false;
        var result = await RunGitAsync($"worktree list --porcelain", cancellationToken);
        var needle = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var needleForward = needle.Replace(Path.DirectorySeparatorChar, '/');
        foreach (var line in result.Stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("worktree ")) continue;
            var candidate = trimmed["worktree ".Length..].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidateForward = candidate.Replace('\\', '/');
            if (string.Equals(candidateForward, needleForward, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task<GitResult> RunGitAsync(string arguments, CancellationToken cancellationToken)
        => await RunGitInAsync(WorkspaceRoot, arguments, cancellationToken);

    private async Task<GitResult> RunGitInAsync(string workingDir, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new GitResult(proc.ExitCode, stdout, stderr);
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    private readonly record struct GitResult(int ExitCode, string Stdout, string Stderr);
}

public readonly record struct DiffStats(string Raw, string Summary);

public enum CommitOutcome { Created, NoChanges }

public sealed record CommitResult(CommitOutcome Outcome, string Message)
{
    public bool HasChanges => Outcome == CommitOutcome.Created;
    public static CommitResult Created(string m) => new(CommitOutcome.Created, m);
    public static CommitResult NoChanges(string m) => new(CommitOutcome.NoChanges, m);
}
