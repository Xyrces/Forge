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
                d[p.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(p.Value.GetRawText())!;
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