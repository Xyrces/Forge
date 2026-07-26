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
/// <strong>Rework detection.</strong> When the issue has both
/// <c>prNumber</c> and <c>reworkAttempts</c> in its metadata (set
/// by the Reviewer / PRWatcher on requeue), this executor syncs the
/// worktree branch to the PR head (<c>origin/agent/&lt;id&gt;</c>)
/// BEFORE the agent runs. This ensures the agent starts on the
/// same commit the PR is at, even if the branch head moved after
/// an external push or a prior rework round. First-time dispatches
/// (no PR metadata) keep the existing behavior: create from the
/// default branch. Conflict rework rounds are unchanged — the
/// agent still merges <c>origin/main</c> itself per the prompt.
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
        var worktreePath = await worktrees.CreateAsync(input.Issue.Id, defaultBranch, ct);
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
            var branch = input.Branch ?? $"agent/{GitRefNames.Sanitize(input.Issue.Id)}";
            var currentMetadata = ParseMetadata(issue.MetadataJson);
            currentMetadata["worktreePath"] = worktreePath;
            currentMetadata["branch"] = branch;

            // Rework detection: if the issue has both prNumber and
            // reworkAttempts in metadata (set by Reviewer/PRWatcher
            // when it requeues for a rework round), sync the worktree
            // branch to the PR head so the agent starts on the same
            // commit the PR is at. First-time dispatches (no PR yet)
            // skip the sync and create from the default branch.
            // Conflict rework rounds stay unchanged — the agent merges
            // origin/main itself per the rework prompt.
            var prNumber = issue.GetMetadata("prNumber");
            var reworkAttempts = issue.GetMetadata("reworkAttempts");
            if (!string.IsNullOrEmpty(prNumber) && int.TryParse(reworkAttempts, out var reworkCount) && reworkCount > 0)
            {
                var remoteRef = $"origin/{branch}";
                logger.LogInformation(
                    "Rework round detected for {Id}: prNumber={Pr} reworkAttempts={Rw}. " +
                    "Syncing worktree branch to PR head via {RemoteRef}",
                    input.Issue.Id, prNumber, reworkAttempts, remoteRef);
                try
                {
                    await worktrees.SyncWorktreeToRefAsync(
                        worktreePath, input.Issue.Id, remoteRef, ct);
                }
                catch (Exception ex)
                {
                    // Sync failure must surface through the workflow
                    // halt guard so the orchestrator can requeue.
                    logger.LogError(ex,
                        "Failed to sync worktree to PR head for rework round {Id}; " +
                        "will not start agent on a stale checkout", input.Issue.Id);
                    throw;
                }
            }

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

    /// <summary>
    /// Strips characters invalid in git ref names from the task id so it
    /// can be safely used as a branch name suffix or remote ref segment.
    /// Reuses the same sanitization logic as <see cref="GitWorktreeService"/>.
    /// </summary>
    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(System.Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
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
