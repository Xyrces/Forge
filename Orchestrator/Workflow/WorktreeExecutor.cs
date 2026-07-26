using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Forge.AgentTools;
using Forge.Core;

namespace Forge.Orchestrator.Workflow;

/// <summary>
/// Second executor in the engineering dispatch workflow. Creates a
/// git worktree on a per-issue branch under the workspace, persists
/// the worktree path / branch / role in the issue's metadata, then
/// returns a <see cref="WorktreeReady"/> with the worktree path +
/// branch + base branch (for the eventual PR's base).
///
/// <para>
/// On a rework round — detected by the presence of both
/// <c>prNumber</c> and <c>reworkAttempts &gt; 0</c> in the issue's
/// metadata — the worktree branch is synced to the PR head
/// (<c>origin/agent/&lt;taskId&gt;</c>) before the agent runs.
/// This ensures the agent always starts from the current PR head,
/// even if the branch advanced between rounds (external pushes,
/// prior round pushed from another checkout, etc.). Conflict rounds
/// stay unchanged: the sync target is always the PR head, and the
/// agent still merges the base branch itself per the prompt.
/// First-time dispatches (no <c>prNumber</c>) keep the current
/// behavior: create the worktree from the default branch.
/// </para>
/// </summary>
public sealed class WorktreeExecutor : FunctionExecutor<ClaimedIssue, WorktreeReady>
{
    private readonly IIssueStore _issues;
    private readonly GitWorktreeService _worktrees;
    private readonly string _defaultBranch;
    private readonly ILogger<WorktreeExecutor> _logger;

    public WorktreeExecutor(
        IIssueStore issues,
        GitWorktreeService worktrees,
        string defaultBranch,
        ILogger<WorktreeExecutor> logger)
        : base(
            "worktree",
            (input, ctx, ct) => HandleAsync(input, issues, worktrees, defaultBranch, logger, ct),
            null,
            new[] { typeof(ClaimedIssue) },
            new[] { typeof(WorktreeReady) })
    {
        _issues = issues;
        _worktrees = worktrees;
        _defaultBranch = defaultBranch;
        _logger = logger;
    }

    public static async ValueTask<WorktreeReady> HandleAsync(
        ClaimedIssue input,
        IIssueStore issues,
        GitWorktreeService worktrees,
        string defaultBranch,
        ILogger logger,
        CancellationToken ct)
    {
        if (input.Result == ClaimResult.AlreadyClaimed)
        {
            logger.LogWarning("WorktreeExecutor received AlreadyClaimed for {Id}", input.Issue.Id);
            return new WorktreeReady(input, WorktreeResult.AlreadyClaimed, null, defaultBranch);
        }

        // Resolve the branch name (same convention throughout the pipeline).
        var branch = input.Branch ?? $"agent/{GitRefNames.Sanitize(input.Issue.Id)}";

        // Create (or reuse existing) worktree from the base branch.
        var worktreePath = await worktrees.CreateAsync(input.Issue.Id, defaultBranch, ct);

        // Detect rework round: prNumber is set and reworkAttempts > 0.
        // The PRWatcher sets both metadata keys when it transitions the
        // task back to Pending for a rework round. On a rework, sync the
        // worktree branch to the PR head (origin/<branch>) so the agent
        // always starts from the current PR head, even if the branch
        // advanced between rounds.
        var prNumber = input.Issue.GetMetadata("prNumber");
        var reworkAttemptsRaw = input.Issue.GetMetadata("reworkAttempts");
        if (!string.IsNullOrEmpty(prNumber)
            && int.TryParse(reworkAttemptsRaw, out var reworkAttempts)
            && reworkAttempts > 0)
        {
            var remoteRef = $"origin/{branch}";
            logger.LogInformation(
                "Rework round detected for {Id} (prNumber={Pr}, reworkAttempts={NAttempts}): " +
                "syncing worktree {Path} to remote ref {RemoteRef}",
                input.Issue.Id, prNumber, reworkAttempts, worktreePath, remoteRef);
            await worktrees.SyncWorktreeToRefAsync(worktreePath, input.Issue.Id, remoteRef, ct);
        }

        // P4 Stage A: advance the dispatch checkpoint BEFORE we
        // touch metadata. If we crash between CreateAsync and the
        // TransitionAsync below, the recoverer sees
        // worktree_acquired + a worktree directory on disk and
        // resumes from RunAgent (the LLM re-runs).
        await issues.SetCheckpointAsync(input.Issue.Id, DispatchCheckpoint.WorktreeAcquired, ct);

        // Persist the path/branch in metadata so the dashboard can
        // surface them even before the agent has run.
        var issue = await issues.GetAsync(input.Issue.Id, ct);
        if (issue is not null)
        {
            var currentMetadata = ParseMetadata(issue.MetadataJson);
            currentMetadata["worktreePath"] = worktreePath;
            currentMetadata["branch"] = branch;

            await issues.TransitionAsync(input.Issue.Id, issue.Status, error: null,
                metadata: currentMetadata, ct: ct);
        }
        return new WorktreeReady(input, WorktreeResult.Ok, worktreePath, defaultBranch);
    }

    private static Dictionary<string, object> ParseMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new();
            var d = new Dictionary<string, object>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                // JSON null = cleared key (delete idiom): absent, not
                // the literal string "null".
                if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                d[p.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(p.Value.GetRawText())!;
            }
            return d;
        }
        catch { return new(); }
    }

}

public enum WorktreeResult
{
    Ok,
    AlreadyClaimed,
}

/// <summary>
/// Output of <see cref="WorktreeExecutor"/>. Carries the claim
/// output (so downstream executors can read IssueRecord + branch)
/// plus the freshly-created worktree path.
/// </summary>
public sealed record WorktreeReady(
    ClaimedIssue Claim,
    WorktreeResult Result,
    string? WorktreePath,
    string BaseBranch);
