using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.AgentTools;

public sealed class GitWorktreeService
{
    private readonly WorkspaceOptions _options;
    private readonly ILogger<GitWorktreeService> _logger;
    // Optional GitHub PAT used to authenticate git push when the
    // global credential.helper (e.g. !gh auth git-credential) can't
    // get a TTY in non-interactive contexts (SCM, recovery, tests).
    // When set, every git process spawned by this service inherits
    // GITHUB_TOKEN in its environment; git honors it for github.com
    // remote operations even with no credential helper present.
    private readonly string? _githubToken;

    public GitWorktreeService(WorkspaceOptions options, ILogger<GitWorktreeService> logger, string? githubToken = null)
    {
        _options = options;
        _logger = logger;
        _githubToken = githubToken;
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

    /// <summary>
    /// Syncs an existing worktree's branch to a given remote ref by
    /// fetching into a per-task ref namespace and resetting --hard.
    /// This is the safe replacement for the removed
    /// <c>SyncWorktreeToDefaultBranchAsync</c> which used a SHARED
    /// refs/forge/sync-base namespace that could clobber PR branches
    /// in rework dispatch. Each task gets its own ref
    /// <c>refs/forge/sync-base/{taskId}</c> so concurrent agent
    /// syncs cannot interfere.
    /// </summary>
    /// <param name="worktreePath">Path to the existing worktree.</param>
    /// <param name="taskId">The task identifier; used to construct the
    /// per-task ref namespace <c>refs/forge/sync-base/{taskId}</c>.</param>
    /// <param name="remoteRef">The remote ref to fetch, e.g.
    /// <c>origin/agent/task-42</c>. The method splits on the first
    /// '/' to extract the remote name and the ref path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown on detached
    /// HEAD, fetch failure, or reset failure.</exception>
    public async Task SyncWorktreeToRefAsync(string worktreePath, string taskId, string remoteRef, CancellationToken cancellationToken = default)
    {
        // Reject detached HEAD -- the agent must be on a branch before syncing.
        var branchResult = await RunGitInAsync(worktreePath, "rev-parse --abbrev-ref HEAD", cancellationToken);
        if (branchResult.ExitCode != 0)
            throw new InvalidOperationException($"git rev-parse --abbrev-ref HEAD failed: {branchResult.Stderr}");

        var branch = branchResult.Stdout.Trim();
        if (branch.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot sync a detached HEAD worktree to a remote ref");

        // Split the remoteRef into remote name and ref path.
        // remoteRef is expected in the form "origin/agent/task-42".
        var slashIndex = remoteRef.IndexOf('/');
        if (slashIndex <= 0)
            throw new InvalidOperationException($"Invalid remoteRef format '{remoteRef}': expected '<remote>/<ref>'");

        var remoteName = remoteRef.Substring(0, slashIndex);
        var refPath = remoteRef.Substring(slashIndex + 1);

        // Use a per-task ref namespace so concurrent syncs don't clobber each other.
        var localRef = $"refs/forge/sync-base/{Sanitize(taskId)}";

        var fetchResult = await RunGitInAsync(worktreePath, $"fetch {remoteName} +{refPath}:{localRef}", cancellationToken);
        if (fetchResult.ExitCode != 0)
            throw new InvalidOperationException($"git fetch remote ref failed (exit={fetchResult.ExitCode}): {fetchResult.Stderr}");

        var resetResult = await RunGitInAsync(worktreePath, $"reset --hard {localRef}", cancellationToken);
        if (resetResult.ExitCode != 0)
            throw new InvalidOperationException($"git reset --hard failed (exit={resetResult.ExitCode}): {resetResult.Stderr}");

        _logger.LogInformation("Synced worktree {Path} branch {Branch} to remote ref {RemoteRef} via {LocalRef}",
            worktreePath, branch, remoteRef, localRef);
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

        // Clean up any per-task sync-base ref created by
        // SyncWorktreeToRefAsync (task-163).  This is best-effort:
        // if the ref does not exist (fresh task, or old shared ref
        // path), git update-ref -d silently returns non-zero which
        // we swallow at debug level.
        var syncRef = "refs/forge/sync-base/" + Sanitize(taskId);
        var delResult = await RunGitAsync("update-ref -d " + syncRef, cancellationToken);
        if (delResult.ExitCode != 0)
        {
            _logger.LogDebug("Sync-base ref {Ref} not present or already deleted ({Exit})",
                syncRef, delResult.ExitCode);
        }
        else
        {
            _logger.LogInformation("Deleted per-task sync-base ref {Ref}", syncRef);
        }
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
        // Guard: never let an agent commit on a protected branch.
        // An agent should always operate on its agent/<taskId>
        // worktree branch; if HEAD is on main (or any non-agent
        // branch) the workflow has misconfigured something and we
        // refuse rather than silently polluting main. The engineer
        // agent must push a branch and open a PR via CommitPushPrExecutor;
        // direct-to-main commits bypass the Reviewer dispatcher entirely.
        var branchResult = await RunGitInAsync(worktreePath, "rev-parse --abbrev-ref HEAD", cancellationToken);
        var currentBranch = branchResult.Stdout.Trim();
        // "HEAD" means detached HEAD (no branch checked out) -- also
        // a misconfiguration: the agent should be on its agent/<id>
        // branch before committing. Refuse rather than allow the
        // commit to land on a detached HEAD that gets fast-forwarded
        // into main.
        if (currentBranch.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || currentBranch.Equals("main", StringComparison.OrdinalIgnoreCase)
            || currentBranch.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to commit on branch '{currentBranch}'. " +
                "Engineer agents must commit on their agent/<taskId> worktree branch " +
                "and open a PR via CommitPushPrExecutor.");
        }
        if (!currentBranch.StartsWith("agent/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to commit on branch '{currentBranch}': must be agent/<taskId>. " +
                "Direct commits outside the agent/* namespace bypass the Reviewer dispatcher.");
        }
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
        // Guard: never push a protected branch from an agent dispatch.
        if (branch.Equals("main", StringComparison.OrdinalIgnoreCase)
            || branch.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to push protected branch '{branch}'. " +
                "Agents must push their agent/<taskId> branch only.");
        }
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
        // Authenticate via env var so the git credential helper doesn't
        // try to prompt on stdin (which hangs in non-interactive
        // contexts like the SCM). GCM and libsecret ignore this, but
        // git's built-in credential resolution reads it for github.com.
        // Also disable the credential helper entirely for this process
        // tree so it can't fall through to a TTY-required helper like
        // `gh auth git-credential`.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (!string.IsNullOrEmpty(_githubToken))
        {
            psi.Environment["GITHUB_TOKEN"] = _githubToken;
        }

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
        => GitRefNames.Sanitize(s);

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
