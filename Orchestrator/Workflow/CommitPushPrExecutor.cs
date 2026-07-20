using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Octokit;
using Forge.AgentTools;
using Forge.Core;
using Forge.Dashboard;

namespace Forge.Orchestrator.Workflow;

/// <summary>
/// Fourth executor in the engineering dispatch workflow.
/// Commits the agent's edits in the worktree, pushes the branch,
/// and opens a PR against the base branch. Updates the issue's
/// metadata with prNumber + branchSha. Returns
/// <see cref="PrOpened"/>.
/// </summary>
public sealed class CommitPushPrExecutor : FunctionExecutor<AgentCompleted, PrOpened>
{
    private readonly IIssueStore _issues;
    private readonly GitWorktreeService _worktrees;
    private readonly GitHubService _gitHub;
    private readonly IDashboardEventBus _events;
    private readonly IMemoryExtractor _memoryExtractor;
    private readonly MemoryExtractionStore _extractionStore;
    private readonly ILogger<CommitPushPrExecutor> _logger;

    public CommitPushPrExecutor(
        IIssueStore issues,
        GitWorktreeService worktrees,
        GitHubService gitHub,
        IDashboardEventBus events,
        IMemoryExtractor memoryExtractor,
        MemoryExtractionStore extractionStore,
        ILogger<CommitPushPrExecutor> logger)
        : base(
            "commit-push-pr",
            (input, ctx, ct) => HandleAsync(input, issues, worktrees, gitHub, events, memoryExtractor, extractionStore, logger, ct),
            null,
            new[] { typeof(AgentCompleted) },
            new[] { typeof(PrOpened) })
    {
        _issues = issues;
        _worktrees = worktrees;
        _gitHub = gitHub;
        _events = events;
        _memoryExtractor = memoryExtractor;
        _extractionStore = extractionStore;
        _logger = logger;
    }

    public static async ValueTask<PrOpened> HandleAsync(
        AgentCompleted input,
        IIssueStore issues,
        GitWorktreeService worktrees,
        GitHubService gitHub,
        IDashboardEventBus events,
        IMemoryExtractor memoryExtractor,
        MemoryExtractionStore extractionStore,
        ILogger logger,
        CancellationToken ct)
    {
        if (input.Result == AgentResult.Skipped)
        {
            return new PrOpened(input, PrResult.Skipped, 0, null);
        }
        var issue = input.Worktree.Claim.Issue;
        var branch = input.Worktree.Claim.Branch ?? $"agent/{issue.Id}";
        var worktreePath = input.Worktree.WorktreePath!;

        var commit = await worktrees.CommitAllAsync(
            worktreePath, $"Task({issue.Id}): {issue.Title}", ct);
        if (!commit.HasChanges)
        {
            logger.LogWarning("Issue {Id}: model produced no diff. Marking Completed.", issue.Id);
            await issues.TransitionAsync(issue.Id, IssueStatus.Completed,
                "no changes (agent made 0 edits)", ct: ct);
            events.Publish(new DashboardEvent(
                DateTime.UtcNow, DashboardEventKind.TaskTransition,
                issue.Id, "Completed (no-op)",
                new Dictionary<string, object?>
                {
                    ["response"] = Truncate(input.Text ?? "", 400),
                }));
            return new PrOpened(input, PrResult.NoDiff, 0, null);
        }

        // P4 Stage A: advance through the dispatch checkpoints so
        // a StartupRecovery pass can resume from push_done if we
        // crash between push and PR-open, or from pr_opened if we
        // crash after the PR is recorded.
        logger.LogInformation("CommitPushPr({Id}): setting CommitDone", issue.Id);
        await issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.CommitDone, ct);
        logger.LogInformation("CommitPushPr({Id}): calling PushAsync", issue.Id);
        await worktrees.PushAsync(worktreePath, branch, ct);
        logger.LogInformation("CommitPushPr({Id}): push done, setting PushDone", issue.Id);
        await issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PushDone, ct);
        var headSha = await worktrees.GetHeadShaAsync(worktreePath, ct);
        logger.LogInformation("CommitPushPr({Id}): got head sha {Sha}", issue.Id, headSha);

        logger.LogInformation("CommitPushPr({Id}): calling CreatePullRequestAsync ({Branch} -> {Base})",
            issue.Id, branch, input.Worktree.BaseBranch);
        var pr = await gitHub.CreatePullRequestAsync(
            title: $"[{issue.Type}] {issue.Title}",
            body: BuildPrBody(issue, headSha, input.Text),
            headBranch: branch,
            baseBranch: input.Worktree.BaseBranch,
            cancellationToken: ct);
        logger.LogInformation("CommitPushPr({Id}): PR #{N} opened", issue.Id, pr.Number);

        await UpdateMetadataAsync(issues, issue.Id, m =>
        {
            m["prNumber"] = pr.Number;
            m["branchSha"] = headSha;
            return m;
        }, ct);
        await issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PrOpened, ct);
        events.Publish(new DashboardEvent(
            DateTime.UtcNow, DashboardEventKind.PrOpened, issue.Id,
            $"PR #{pr.Number} -> {branch}",
            new Dictionary<string, object?>
            {
                ["prNumber"] = pr.Number,
                ["branch"] = branch,
                ["sha"] = headSha,
            }));
        logger.LogInformation("Opened PR #{PrNumber} for {Id}", pr.Number, issue.Id);

        // P5.5: extract durable project memory from the model's
        // response. Advisory only; failure must not fail the
        // dispatch. Runs after the pr_opened checkpoint so a
        // restart from this point skips extraction (the
        // MemoryStore upsert is also idempotent on the namespaced
        // key, so a second pass is safe).
        try
        {
            var extraction = await memoryExtractor.ExtractAsync(
                issue.Id, input.Text, ct);
            try
            {
                await extractionStore.RecordAsync(extraction, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "MemoryExtractionStore.RecordAsync failed for {Id}; continuing",
                    issue.Id);
            }
            events.Publish(new DashboardEvent(
                DateTime.UtcNow, DashboardEventKind.MemoryExtracted, issue.Id,
                extraction.Error is null
                    ? $"Extracted {extraction.ExtractedCount} memory(s) from {extraction.SourceChars} chars"
                    : $"Memory extraction failed: {extraction.Error}",
                new Dictionary<string, object?>
                {
                    ["sourceChars"] = extraction.SourceChars,
                    ["extractedCount"] = extraction.ExtractedCount,
                    ["persistedKeys"] = extraction.PersistedKeys,
                    ["error"] = extraction.Error,
                }));
            if (extraction.Error is null && extraction.ExtractedCount > 0)
            {
                logger.LogInformation(
                    "Extracted {N} memory(s) for {Id}", extraction.ExtractedCount, issue.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Memory extraction raised for {Id}; dispatch continues", issue.Id);
        }

        return new PrOpened(input, PrResult.Ok, pr.Number, headSha);
    }

    private static string BuildPrBody(IssueRecord issue, string headSha, string? modelText)
        => $"Task: {issue.Id}\n\nSHA: {headSha}\n\n## Model response\n\n{modelText ?? string.Empty}";

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static async Task UpdateMetadataAsync(
        IIssueStore issues, string id,
        Func<Dictionary<string, object>, Dictionary<string, object>> mutate,
        CancellationToken ct)
    {
        var cur = await issues.GetAsync(id, ct);
        if (cur is null) return;
        var current = ParseMetadata(cur.MetadataJson);
        var next = mutate(current);
        await issues.TransitionAsync(id, cur.Status, error: null, metadata: next, ct: ct);
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

public enum PrResult
{
    Ok,
    NoDiff,
    Skipped,
}

public sealed record PrOpened(
    AgentCompleted Agent,
    PrResult Result,
    int PrNumber,
    string? BranchSha);