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
        ILogger<CommitPushPrExecutor> logger,
        Forge.Core.Workflow.WorkflowResolver? workflow = null)
        : base(
            "commit-push-pr",
            (input, ctx, ct) => HandleAsync(input, issues, worktrees, gitHub, events, memoryExtractor, extractionStore, logger, workflow, ct),
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

    /// <summary>Circuit breaker for no-progress runs (no diff and no
    /// explicit NO_CHANGES_NEEDED): requeue this many times before
    /// failing the task for the operator.</summary>
    public const int MaxNoProgressAttempts = 3;

    public static async ValueTask<PrOpened> HandleAsync(
        AgentCompleted input,
        IIssueStore issues,
        GitWorktreeService worktrees,
        GitHubService gitHub,
        IDashboardEventBus events,
        IMemoryExtractor memoryExtractor,
        MemoryExtractionStore extractionStore,
        ILogger logger,
        Forge.Core.Workflow.WorkflowResolver? workflow,
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
        // An agent that commits its own work via bash during the run
        // (the prompt's contract) leaves "nothing to commit" for
        // CommitAllAsync — which is NOT the same as "no work
        // produced". Check whether the branch is actually ahead of
        // base before declaring a no-diff run. (Observed live
        // 2026-07-24: task-155's run committed +149 lines of real
        // tests, then got requeued as 'no diff (attempt 1)' and was
        // two strikes from Failed despite the work being real.)
        var hasChanges = commit.HasChanges;
        if (!hasChanges)
        {
            var ahead = await worktrees.GetDiffStatsAsync(worktreePath, input.Worktree.BaseBranch, ct);
            if (!string.IsNullOrWhiteSpace(ahead.Summary))
            {
                hasChanges = true;
                logger.LogInformation(
                    "CommitPushPr({Id}): nothing to commit but branch is ahead of {Base} — agent self-committed ({Summary}); proceeding to push/PR",
                    issue.Id, input.Worktree.BaseBranch, ahead.Summary);
            }
        }
        if (!hasChanges)
        {
            // Stale-dispatch guard: a long agent run can finish AFTER
            // the watch already merged this task's PR (the rework
            // loop reuses the branch). Never stomp a terminal state
            // with a fresh transition.
            var current = await issues.GetAsync(issue.Id, ct);
            if (current?.Status is IssueStatus.Completed or IssueStatus.Failed or IssueStatus.Closed)
            {
                logger.LogInformation(
                    "Issue {Id}: no diff, but the task is already {Status} (watch closed the loop meanwhile) — leaving it alone",
                    issue.Id, current!.Status);
                return new PrOpened(input, PrResult.NoDiff, 0, null);
            }

            // A no-diff run is only a legitimate completion when the
            // agent EXPLICITLY concluded no changes were needed (the
            // prompt's completion contract). Anything else — iteration-
            // cap truncation, stuck-in-exploration loops — is a failed
            // attempt: requeue with a circuit breaker. (Observed live:
            // all six tasks of a sprint hollow-completed when the MAF
            // 40-iteration default cut every run during exploration.)
            var explicitNoOp = (input.Text ?? "")
                .Contains("NO_CHANGES_NEEDED", StringComparison.OrdinalIgnoreCase);
            // Workflow policy noDiffOutcome=rework (pass 3): the
            // operator doesn't accept verified no-op completions —
            // even an explicit NO_CHANGES_NEEDED requeues (the
            // no-progress circuit breaker still caps the loop).
            var noDiffOutcome = "completed";
            if (workflow is not null)
            {
                var definition = await workflow.ResolveAsync(ct);
                noDiffOutcome = Forge.Core.Workflow.WorkflowPolicyReader.GetString(
                    definition, Forge.Core.Workflow.WorkflowPolicies.NoDiffOutcome, "completed");
            }
            if (!explicitNoOp || string.Equals(noDiffOutcome, "rework", StringComparison.Ordinal))
            {
                var attempts = int.TryParse(current?.GetMetadata("noProgressAttempts"), out var n) ? n + 1 : 1;
                if (attempts >= MaxNoProgressAttempts)
                {
                    await issues.TransitionAsync(issue.Id, IssueStatus.Failed,
                        $"agent produced no diff in {attempts} attempts (last response truncated)",
                        new Dictionary<string, object> { ["noProgressAttempts"] = attempts.ToString() }, ct);
                    events.Publish(new DashboardEvent(
                        DateTime.UtcNow, DashboardEventKind.TaskTransition,
                        issue.Id, $"Failed (no progress in {attempts} attempts)",
                        new Dictionary<string, object?> { ["response"] = Truncate(input.Text ?? "", 400) }));
                    logger.LogError("Issue {Id}: no diff after {Attempts} attempts — Failed for operator review", issue.Id, attempts);
                }
                else
                {
                    await issues.TransitionAsync(issue.Id, IssueStatus.Pending,
                        explicitNoOp
                            ? $"NO_CHANGES_NEEDED rejected by workflow policy noDiffOutcome=rework (attempt {attempts})"
                            : $"no diff without NO_CHANGES_NEEDED (attempt {attempts})",
                        new Dictionary<string, object> { ["noProgressAttempts"] = attempts.ToString() }, ct);
                    events.Publish(new DashboardEvent(
                        DateTime.UtcNow, DashboardEventKind.TaskTransition,
                        issue.Id, $"Requeued (no progress, attempt {attempts})",
                        new Dictionary<string, object?> { ["response"] = Truncate(input.Text ?? "", 400) }));
                    logger.LogWarning("Issue {Id}: no diff without NO_CHANGES_NEEDED — requeued (attempt {Attempts})", issue.Id, attempts);
                }
                return new PrOpened(input, PrResult.NoDiff, 0, null);
            }
            logger.LogInformation(
                "Issue {Id}: agent explicitly concluded NO_CHANGES_NEEDED. Marking Completed.", issue.Id);
            await issues.TransitionAsync(issue.Id, IssueStatus.Completed,
                "no changes needed (agent verified)", ct: ct);
            await UpdateMetadataAsync(issues, issue.Id, m =>
            {
                m["lastError"] = null!;
                m["lastErrorAt"] = null!;
                return m;
            }, ct);
            events.Publish(new DashboardEvent(
                DateTime.UtcNow, DashboardEventKind.TaskTransition,
                issue.Id, "Completed (verified no-op)",
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

        // Rework loop: the issue may already carry a prNumber (its
        // earlier dispatch opened the PR; this run pushed new commits
        // to the same branch). Reuse that PR — creating a second one
        // for the same branch is rejected by GitHub.
        var existingPrText = issue.GetMetadata("prNumber");
        Octokit.PullRequest pr;
        if (int.TryParse(existingPrText, out var existingPrNumber))
        {
            logger.LogInformation("CommitPushPr({Id}): rework — reusing PR #{Pr} (push updated the branch)", issue.Id, existingPrNumber);
            pr = await gitHub.GetPullRequestAsync(existingPrNumber, ct);
        }
        else
        {
            // Orphan-PR reuse: the task never recorded a prNumber —
            // the PR for this branch was opened OUTSIDE the pipeline
            // (operator hand-created it, it was adopted via
            // /adopt-pr, or the metadata was lost on a requeue).
            // Creating a second PR for the same branch is a 422 that
            // MAF's InProcessExecution swallows, leaving a silent
            // mid-pipeline halt + infinite requeue loop (observed
            // live 2026-07-25 on task-155 / PR #32). Look the branch
            // up FIRST.
            var existing = await gitHub.GetOpenPullRequestForBranchAsync(branch, ct);
            if (existing is not null)
            {
                logger.LogInformation("CommitPushPr({Id}): reusing existing open PR #{Pr} found by branch (no prNumber metadata)", issue.Id, existing.Number);
                pr = existing;
            }
            else
            {
                logger.LogInformation("CommitPushPr({Id}): calling CreatePullRequestAsync ({Branch} -> {Base})",
                    issue.Id, branch, input.Worktree.BaseBranch);
                pr = await gitHub.CreatePullRequestAsync(
                    title: $"[{issue.Type}] {issue.Title}",
                    body: BuildPrBody(issue, headSha, input.Text),
                    headBranch: branch,
                    baseBranch: input.Worktree.BaseBranch,
                    cancellationToken: ct);
                logger.LogInformation("CommitPushPr({Id}): PR #{N} opened", issue.Id, pr.Number);
            }
        }

        await UpdateMetadataAsync(issues, issue.Id, m =>
        {
            m["prNumber"] = pr.Number;
            m["branchSha"] = headSha;
            // Success clears any stale run-failure record (requeues
            // never remove it; metadata is upsert-merge only, so
            // JSON null is the delete idiom).
            m["lastError"] = null!;
            m["lastErrorAt"] = null!;
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