using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Octokit;
using Forge.AgentTools;
using Forge.Core;
using Forge.Dashboard;

namespace Forge.Orchestrator;

/// <summary>
/// P4 Stage A — checkpoint-based recovery. Runs at startup
/// BEFORE the dispatch loop. See <c>docs/p4-restart-safety.md</c>.
///
/// <para>
/// For every issue in <c>status=InProgress + assignee=forge</c>:
/// inspect the <c>dispatch_checkpoint</c> + the worktree
/// directory + the metadata, then either replay the
/// side-effect the previous run didn't get to, fail the issue,
/// or leave it alone. Writes one <c>recovery_report</c> row
/// at the end of the pass.
/// </para>
///
/// <para>
/// The recoverer does NOT re-run the LLM. The agent's
/// conversation history may be lost on a hard crash (that's a
/// Stage B / Durable Task problem); what the recoverer does
/// is finish the cheap side-effects (commit, push, PR open,
/// enqueue watch) when the LLM has already finished.
/// </para>
/// </summary>
public sealed class StartupRecovery
{
    private readonly IIssueStore _issues;
    private readonly RecoveryReportStore _reports;
    private readonly GitWorktreeService _worktrees;
    private readonly IGitHubRecovery _gitHub;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<StartupRecovery> _logger;
    private readonly StartupRecoveryOptions _options;

    public StartupRecovery(
        IIssueStore issues,
        RecoveryReportStore reports,
        GitWorktreeService worktrees,
        IGitHubRecovery gitHub,
        IDashboardEventBus events,
        ILogger<StartupRecovery> logger,
        StartupRecoveryOptions? options = null,
        Core.TaskStateMachine? lifecycle = null)
    {
        _issues = issues;
        _reports = reports;
        _worktrees = worktrees;
        _gitHub = gitHub;
        _events = events;
        _logger = logger;
        _options = options ?? new StartupRecoveryOptions();
        _lifecycle = lifecycle;
    }

    private readonly Core.TaskStateMachine? _lifecycle;

    /// <summary>Everything the recovery pass needs that is
    /// project-scoped. The construction-time fields form the PRIMARY
    /// context; Program.cs passes one context per additional
    /// registered project (multi-project fix 2026-07-29: a restart
    /// stranded a second project's InProgress tasks forever because
    /// recovery only ever scanned the primary store — scanned=0
    /// while porthorizon task-5/6 sat claimed with no run).</summary>
    public sealed record ProjectRecoveryContext(
        string ProjectId,
        IIssueStore Issues,
        GitWorktreeService Worktrees,
        IGitHubRecovery GitHub,
        string? DefaultBranch = null);

    private ProjectRecoveryContext PrimaryContext =>
        new("<primary>", _issues, _worktrees, _gitHub);

    /// <summary>Report an observed event to the lifecycle machine
    /// (best-effort — never breaks a recovery pass).</summary>
    /// <summary>Engineering claim identities: the legacy literal plus
    /// every slot-role name (the claim identity is the role since
    /// 2026-08-01). Anything else (e.g. a human assignee) is not the
    /// orchestrator's to recover.</summary>
    internal static bool IsEngineeringClaimant(string assignee)
        => string.Equals(assignee, "forge", StringComparison.Ordinal)
           || Agents.RoleAgentRegistry.AllSlotRoles.Contains(assignee, StringComparer.Ordinal);

    private async Task ReportLifecycleAsync(IssueRecord issue, Core.TaskEvent evt, ProjectRecoveryContext ctx, CancellationToken ct)
    {
        if (_lifecycle is null) return;
        try
        {
            var fresh = await ctx.Issues.GetAsync(issue.Id, ct) ?? issue;
            await _lifecycle.ReportAsync(ctx.Issues, fresh, evt, watch: null, hasActiveDevRun: false, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lifecycle report {Event} failed for {Id}; continuing", evt, issue.Id);
        }
    }

    /// <summary>
    /// A requeue whose recorded lifecycle state is TERMINAL (Failed /
    /// BlockedOperator — possible when a terminal-failed task was
    /// reclaimed without a machine requeue report) must be coerced
    /// back through the machine: Failed+Dispatched is an illegal
    /// transition, so without this the state stays Failed for the
    /// task's whole next round and the board contradicts itself
    /// (observed live 2026-08-01: task-18). OperatorRequeue is the
    /// table's Failed/BlockedOperator → Pending entry — the requeue
    /// IS that event regardless of who triggered it.
    /// </summary>
    private async Task CoerceTerminalStateAsync(IssueRecord issue, ProjectRecoveryContext ctx, CancellationToken ct)
    {
        var state = issue.GetMetadata("state");
        if (state is nameof(Core.TaskLifecycleState.Failed) or nameof(Core.TaskLifecycleState.BlockedOperator))
        {
            await ReportLifecycleAsync(issue, Core.TaskEvent.OperatorRequeue, ctx, ct);
        }
    }

    public StartupRecoveryOptions Options => _options;

    /// <summary>
    /// Sweep every in-progress forge issue in the primary store AND
    /// every supplied project context. Returns the
    /// <see cref="RecoveryReportRecord"/> id of the audit row.
    /// </summary>
    public async Task<long> RunAsync(
        string? specId = null,
        IReadOnlyList<ProjectRecoveryContext>? extraProjects = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var report = await _reports.StartAsync(specId, ct);
        _logger.LogInformation("StartupRecovery: starting report {Id} (specId={Spec}, projects={Count})",
            report.Id, specId ?? "<all>", 1 + (extraProjects?.Count ?? 0));

        var scanned = 0;
        var replayed = 0;
        var failed = 0;
        var actions = new List<RecoveryActionRecord>();
        var contexts = new List<ProjectRecoveryContext> { PrimaryContext };
        if (extraProjects is not null) contexts.AddRange(extraProjects);
        try
        {
            foreach (var ctx in contexts)
            {
                // Zombie run rows cannot be alive — this process just
                // started. Closing them is load-bearing beyond board
                // truth: the orphan-claim reaper treats an active
                // agent_run row as proof the task's claim is alive,
                // so a zombie shields its orphaned claim forever
                // (observed live 2026-08-01: reviewer rows 9h old).
                if (ctx.Issues is Core.IssueStore concrete)
                {
                    var zombies = await new Core.AgentRunStore(concrete.Db)
                        .FailZombieRunsAsync(null, "service restart — no run survives a process restart", ct);
                    foreach (var z in zombies)
                    {
                        _logger.LogInformation("StartupRecovery: closed zombie agent run {RunId} (project={Project})", z, ctx.ProjectId);
                    }
                }

                // The store returns every held InProgress row
                // (assignee = live-hold). Recovery acts only on
                // ENGINEERING claims — the legacy "forge" identity or
                // a slot-role name (coredev/clientdev/qa/reviewer/
                // pipeline). A human-held row is durable intent, not
                // a dead run: leave it alone.
                var candidates = (await ctx.Issues.ListInProgressForRecoveryAsync(ct))
                    .Where(i => i.Assignee is not null && IsEngineeringClaimant(i.Assignee))
                    .ToList();
                scanned += candidates.Count;
                foreach (var issue in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (specId is not null && issue.GetMetadata("specId") != specId) continue;
                    var action = await ReplayAsync(issue, ctx, ct);
                    actions.Add(action);
                    if (action.Action == "replay") replayed++;
                    else if (action.Action == "failed") failed++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartupRecovery: pass crashed mid-sweep");
            failed++;
            actions.Add(new RecoveryActionRecord("<sweep>", null, null, "failed", ex.Message));
        }

        await _reports.FinishAsync(
            report.Id, scanned, replayed, failed, actions, sw.Elapsed, ct);

        _logger.LogInformation(
            "StartupRecovery: pass done in {Ms}ms (scanned={Scanned} replayed={Replayed} failed={Failed})",
            sw.ElapsedMilliseconds, scanned, replayed, failed);

        return report.Id;
    }

    /// <summary>
    /// Inspect one issue and replay the next side-effect (primary
    /// context). Returns a <see cref="RecoveryActionRecord"/>
    /// describing what the recoverer did.
    /// </summary>
    public Task<RecoveryActionRecord> ReplayAsync(IssueRecord issue, CancellationToken ct = default)
        => ReplayAsync(issue, PrimaryContext, ct);

    private async Task<RecoveryActionRecord> ReplayAsync(IssueRecord issue, ProjectRecoveryContext ctx, CancellationToken ct)
    {
        var before = issue.DispatchCheckpoint?.ToDbValue();
        // Per-issue timeout: a single stuck GitHub API call or git
        // push must not block the entire startup. 60s is enough for
        // a healthy PR creation; if it takes longer, log + skip +
        // let the next dispatch tick try.
        using var issueCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        issueCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            // Classify first — pure function over metadata + worktree.
            var decision = Classify(issue);
            switch (decision.Action)
            {
                case RecoveryAction.LeftAlone:
                    _logger.LogInformation("Recovery({Id}): left alone — {Reason}", issue.Id, decision.Reason);
                    _events.Publish(RecoveryEvent(issue.Id, "left_alone", decision.Reason));
                    return new RecoveryActionRecord(issue.Id, before, before, "left_alone", decision.Reason);

                case RecoveryAction.Failed:
                    _logger.LogWarning("Recovery({Id}): failing — {Reason}", issue.Id, decision.Reason);
                    await ctx.Issues.IncrementRecoveryAttemptsAsync(issue.Id, issueCts.Token);
                    if (issue.RecoveryAttempts + 1 >= _options.MaxAttempts)
                    {
                        // Hard fail: clean up worktree + transition to Failed.
                        await ReportLifecycleAsync(issue, Core.TaskEvent.BreakerTripped, ctx, issueCts.Token);
                        await TryRemoveWorktreeAsync(issue, ctx, issueCts.Token);
                        await ctx.Issues.TransitionAsync(issue.Id, IssueStatus.Failed,
                            $"recovered: {decision.Reason}", metadata: new Dictionary<string, object>
                            {
                                ["lastError"] = $"recovered: {decision.Reason}",
                                ["recoveryFailedAt"] = DateTime.UtcNow.ToString(IssueStore.DateFormat),
                            }, ct: issueCts.Token);
                        _events.Publish(RecoveryEvent(issue.Id, "failed", decision.Reason));
                        return new RecoveryActionRecord(issue.Id, before, null, "failed", decision.Reason);
                    }
                    // Soft: leave in InProgress; the dispatch loop
                    // will re-attempt on the next tick. Reset
                    // checkpoint to the issue's pre-recovery state
                    // so the next attempt restarts cleanly.
                    return new RecoveryActionRecord(issue.Id, before, before, "left_alone",
                        $"retry: {decision.Reason}");

                case RecoveryAction.Replay:
                    // The crashed run is a died run in lifecycle
                    // terms (its work may be partially committed);
                    // the replay's re-dispatch reports Dispatched.
                    await ReportLifecycleAsync(issue, Core.TaskEvent.RunDied, ctx, issueCts.Token);
                    return await ReplayFromCheckpointAsync(issue, decision, ctx, issueCts.Token);

                default:
                    return new RecoveryActionRecord(issue.Id, before, before, "left_alone",
                        $"unknown decision {decision.Action}");
            }
        }
        catch (OperationCanceledException) when (issueCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Per-issue timeout tripped. Log + leave alone so the
            // dispatch loop can re-attempt later.
            _logger.LogWarning(
                "Recovery({Id}) timed out after 60s; leaving for dispatch loop", issue.Id);
            return new RecoveryActionRecord(issue.Id, before, before, "failed",
                "recovery timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery({Id}): crashed", issue.Id);
            return new RecoveryActionRecord(issue.Id, before, before, "failed", ex.Message);
        }
    }

    private async Task<RecoveryActionRecord> ReplayFromCheckpointAsync(
        IssueRecord issue, RecoveryDecision decision, ProjectRecoveryContext ctx, CancellationToken ct)
    {
        var before = issue.DispatchCheckpoint?.ToDbValue();
        var worktreePath = issue.GetMetadata("worktreePath");
        var branch = issue.GetMetadata("branch") ?? $"agent/{issue.Id}";

        // Claimed (or no checkpoint at all — a terminal transition
        // cleared it): nothing usable was acquired — re-queue
        // without the worktree requirement (there IS no worktree at
        // this point; the missing-worktree failure below is for
        // checkpoints that already recorded one).
        if (issue.DispatchCheckpoint is null or Core.DispatchCheckpoint.Claimed)
        {
            await ctx.Issues.TransitionAsync(issue.Id, IssueStatus.Pending,
                "recovery: orphaned just after claim by restart; re-queued", ct: ct);
            await CoerceTerminalStateAsync(issue, ctx, ct);
            _events.Publish(RecoveryEvent(issue.Id, "requeued",
                "claimed; transitioned to Pending for re-dispatch"));
            return new RecoveryActionRecord(issue.Id, before, before, "requeued", null);
        }

        _logger.LogInformation("Recovery({Id}): replaying from {Cp} on branch {Branch}",
            issue.Id, before, branch);
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            // The recoverer can't replay without the worktree. Fail it.
            await ctx.Issues.TransitionAsync(issue.Id, IssueStatus.Failed,
                $"recovered: worktree missing at {worktreePath}",
                metadata: new Dictionary<string, object>
                {
                    ["lastError"] = $"recovered: worktree missing at {worktreePath}",
                }, ct: ct);
            _events.Publish(RecoveryEvent(issue.Id, "failed", "worktree missing"));
            return new RecoveryActionRecord(issue.Id, before, null, "failed",
                $"worktree missing at {worktreePath}");
        }

        try
        {
            switch (issue.DispatchCheckpoint)
            {
                case DispatchCheckpoint.WorktreeAcquired:
                    // We don't re-run the LLM here (would require
                    // loading the AgentSession which is Stage B).
                    // The cheapest correct thing is: transition the
                    // issue back to Pending so the next dispatch tick
                    // re-claims it and re-runs from scratch. For
                    // worktree_acquired the worktree already exists,
                    // so re-acquire is a no-op. (Previously this left
                    // the issue InProgress assuming the loop
                    // re-claims it — but the loop only claims Pending
                    // issues, so those orphans sat forever.)
                    await ctx.Issues.TransitionAsync(issue.Id, IssueStatus.Pending,
                        $"recovery: orphaned at {issue.DispatchCheckpoint?.ToDbValue()} by restart; re-queued", ct: ct);
                    _events.Publish(RecoveryEvent(issue.Id, "requeued",
                        $"{issue.DispatchCheckpoint?.ToDbValue()}; transitioned to Pending for re-dispatch"));
                    return new RecoveryActionRecord(issue.Id, before, before, "requeued",
                        "worktree_acquired; transitioned to Pending for re-dispatch");

                case DispatchCheckpoint.AgentCompleted:
                    // Commit + push + PR.
                    await CommitAndPushAsync(issue, worktreePath!, branch, ctx, ct);
                    await TryOpenPrAsync(issue, branch, ctx, ct);
                    await ctx.Issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PrOpened, ct);
                    await ctx.Issues.IncrementRecoveryAttemptsAsync(issue.Id, ct);
                    _events.Publish(RecoveryEvent(issue.Id, "replay", "agent_completed -> pr_opened"));
                    return new RecoveryActionRecord(issue.Id, before, DispatchCheckpoint.PrOpened.ToDbValue(), "replay", null);

                case DispatchCheckpoint.CommitDone:
                    _logger.LogInformation("Recovery({Id}): CommitDone step - pushing", issue.Id);
                    await ctx.Worktrees.PushAsync(worktreePath!, branch, ct);
                    _logger.LogInformation("Recovery({Id}): push OK - opening PR", issue.Id);
                    await TryOpenPrAsync(issue, branch, ctx, ct);
                    _logger.LogInformation("Recovery({Id}): PR OK", issue.Id);
                    await ctx.Issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PrOpened, ct);
                    await ctx.Issues.IncrementRecoveryAttemptsAsync(issue.Id, ct);
                    _events.Publish(RecoveryEvent(issue.Id, "replay", "commit_done -> pr_opened"));
                    return new RecoveryActionRecord(issue.Id, before, DispatchCheckpoint.PrOpened.ToDbValue(), "replay", null);

                case DispatchCheckpoint.PushDone:
                    await TryOpenPrAsync(issue, branch, ctx, ct);
                    await ctx.Issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PrOpened, ct);
                    await ctx.Issues.IncrementRecoveryAttemptsAsync(issue.Id, ct);
                    _events.Publish(RecoveryEvent(issue.Id, "replay", "push_done -> pr_opened"));
                    return new RecoveryActionRecord(issue.Id, before, DispatchCheckpoint.PrOpened.ToDbValue(), "replay", null);

                case DispatchCheckpoint.PrOpened:
                    // Engineering-owed rework round whose run died
                    // (Classify only routes prNumber tasks here when
                    // the lifecycle state is NOT a PR-phase state).
                    // Nothing to replay in git — PR/branch/worktree
                    // all exist; the round re-runs as a fresh
                    // engineering dispatch with its rework context
                    // intact. Re-queue like worktree_acquired.
                    await ctx.Issues.TransitionAsync(issue.Id, IssueStatus.Pending,
                        "recovery: orphaned rework round (run died with PR open); re-queued", ct: ct);
                    await CoerceTerminalStateAsync(issue, ctx, ct);
                    _events.Publish(RecoveryEvent(issue.Id, "requeued",
                        "pr_opened + engineering-owed state; transitioned to Pending for rework re-dispatch"));
                    return new RecoveryActionRecord(issue.Id, before, before, "requeued", null);

                default:
                    return new RecoveryActionRecord(issue.Id, before, before, "left_alone",
                        $"no replay path for checkpoint {before}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery({Id}): replay threw", issue.Id);
            return new RecoveryActionRecord(issue.Id, before, before, "failed", ex.Message);
        }
    }

    /// <summary>
    /// Pure classification function: given an issue's checkpoint
    /// + metadata + worktree directory existence, return what
    /// the recoverer should do. Exposed so unit tests can drive
    /// every classification branch without touching the
    /// IIssueStore / GitHubService.
    /// </summary>
    public RecoveryDecision Classify(IssueRecord issue)
    {
        var worktreePath = issue.GetMetadata("worktreePath");
        var worktreeExists = !string.IsNullOrWhiteSpace(worktreePath) && Directory.Exists(worktreePath!);
        var branch = issue.GetMetadata("branch") ?? $"agent/{issue.Id}";
        var prNumber = issue.GetMetadata("prNumber");

        // Already has a PR AND is in a watch-owned (PR-phase) state —
        // leave alone; the watch path is independent of the workflow.
        // A prNumber task in an ENGINEERING-owed state (ReworkQueued
        // etc.) is a rework round whose run died — fall THROUGH to
        // checkpoint classification so it re-queues (observed live
        // 2026-07-31: task-360/361/362/364 orphaned InProgress +
        // ReworkQueued, "left alone" at every boot while nothing
        // re-dispatched them).
        var state = issue.GetMetadata("state");
        if (!string.IsNullOrEmpty(prNumber) && int.TryParse(prNumber, out _)
            && Core.OrphanedClaimReaper.IsWatchOwnedPhase(state))
        {
            return new RecoveryDecision(RecoveryAction.LeftAlone, "prNumber already recorded");
        }

        // No checkpoint recorded — either a pre-v11 legacy row or a
        // task that went terminal (terminal transitions CLEAR the
        // checkpoint) and was later requeued + reclaimed. At boot no
        // runs are alive and the dispatch loop only claims Pending,
        // so "leave it" strands it exactly like the claimed case
        // (observed live 2026-08-01: task-18 — Failed cleared the
        // checkpoint, strike-reset reclaimed it, restart orphaned
        // it). Re-queue for re-dispatch.
        if (issue.DispatchCheckpoint is null)
        {
            return new RecoveryDecision(RecoveryAction.Replay,
                "no checkpoint recorded; re-queue orphaned claim for re-dispatch");
        }

        switch (issue.DispatchCheckpoint)
        {
            case DispatchCheckpoint.Claimed:
                // At boot there are NO live runs (recovery executes
                // before dispatch starts), so a claimed task is by
                // definition orphaned — and the dispatch loop only
                // claims Pending, so "leave it" strands it until the
                // 30-min orphan reaper (observed live 2026-07-31:
                // task-18). Re-queue immediately; nothing was
                // acquired yet, so re-dispatch starts clean.
                return new RecoveryDecision(RecoveryAction.Replay,
                    "just-claimed orphan at boot; re-queue for re-dispatch");

            case DispatchCheckpoint.WorktreeAcquired:
                if (!worktreeExists)
                    return new RecoveryDecision(RecoveryAction.Failed,
                        "worktree_acquired but directory missing");
                // Replay re-queues to Pending so the dispatch loop
                // re-claims (the loop never claims InProgress
                // issues, so leaving it would orphan the task).
                return new RecoveryDecision(RecoveryAction.Replay,
                    "worktree exists; re-queue for LLM re-run");

            case DispatchCheckpoint.AgentCompleted:
            case DispatchCheckpoint.CommitDone:
            case DispatchCheckpoint.PushDone:
                if (!worktreeExists)
                    return new RecoveryDecision(RecoveryAction.Failed,
                        $"{issue.DispatchCheckpoint} but worktree missing at {worktreePath}");
                return new RecoveryDecision(RecoveryAction.Replay,
                    $"replay from {issue.DispatchCheckpoint}");

            case DispatchCheckpoint.PrOpened:
                // Reached with a prNumber only when the gate above
                // determined the task is ENGINEERING-owed (rework
                // round — state ReworkQueued etc.) and its run died:
                // re-queue it like the worktree_acquired replay. The
                // dispatch loop never claims InProgress, so leaving
                // it orphans the round (observed live 2026-07-31:
                // task-360/361/362/364 "left alone" at every boot).
                if (!string.IsNullOrEmpty(prNumber))
                    return new RecoveryDecision(RecoveryAction.Replay,
                        "engineering-owed rework round (prNumber + non-PR-phase state); re-queue for re-dispatch");
                // pr_opened but no prNumber — odd, leave alone and
                // let the dispatch loop figure it out.
                return new RecoveryDecision(RecoveryAction.LeftAlone,
                    "pr_opened checkpoint but no prNumber; dispatch loop will re-handle");

            default:
                return new RecoveryDecision(RecoveryAction.Failed, $"unknown checkpoint {issue.DispatchCheckpoint}");
        }
    }

    private async Task CommitAndPushAsync(
        IssueRecord issue, string worktreePath, string branch, ProjectRecoveryContext ctx, CancellationToken ct)
    {
        var commit = await ctx.Worktrees.CommitAllAsync(
            worktreePath, $"Task({issue.Id}): {issue.Title}", ct);
        if (!commit.HasChanges)
        {
            _logger.LogInformation("Recovery({Id}): no changes to commit on {Branch}", issue.Id, branch);
        }
        else
        {
            _logger.LogInformation("Recovery({Id}): committed on {Branch}", issue.Id, branch);
        }
        await ctx.Worktrees.PushAsync(worktreePath, branch, ct);
        var headSha = await ctx.Worktrees.GetHeadShaAsync(worktreePath, ct);
        await ctx.Issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.CommitDone, ct);
        await ctx.Issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PushDone, ct);
        // Record branchSha so the dispatch loop's PR step can pick it up.
        await ctx.Issues.TransitionAsync(issue.Id, issue.Status, error: null,
            metadata: new Dictionary<string, object> { ["branchSha"] = headSha }, ct: ct);
    }

    private async Task TryOpenPrAsync(IssueRecord issue, string branch, ProjectRecoveryContext ctx, CancellationToken ct)
    {
        var pr = await ctx.GitHub.CreatePullRequestAsync(
            title: $"[{issue.Type}] {issue.Title}",
            body: $"Task: {issue.Id}\n\n(recovered by StartupRecovery after a crash)\n",
            headBranch: branch,
            baseBranch: ctx.DefaultBranch ?? "main",
            cancellationToken: ct);
        await ctx.Issues.TransitionAsync(issue.Id, issue.Status, error: null,
            metadata: new Dictionary<string, object>
            {
                ["prNumber"] = pr.Number,
                ["prOpenedAt"] = DateTime.UtcNow.ToString("O"),
            }, ct: ct);
        _logger.LogInformation("Recovery({Id}): opened PR #{Pr}", issue.Id, pr.Number);

        // No watch row: the state-driven sweep discovers watched
        // tasks by their prNumber metadata (written above).
    }

    private async Task TryRemoveWorktreeAsync(IssueRecord issue, ProjectRecoveryContext ctx, CancellationToken ct)
    {
        try { await ctx.Worktrees.RemoveAsync(issue.Id, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Recovery({Id}): worktree removal failed", issue.Id); }
    }

    private DashboardEvent RecoveryEvent(string issueId, string action, string? detail)
        => new(DateTime.UtcNow, DashboardEventKind.RecoveryAction, issueId, detail,
            new Dictionary<string, object?> { ["action"] = action });
}

public sealed record StartupRecoveryOptions
{
    /// <summary>
    /// Maximum number of recovery attempts before a "fail"
    /// decision hard-transitions the issue to Failed. Default 3.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;
}

public enum RecoveryAction
{
    Replay,
    Failed,
    LeftAlone,
}

public sealed record RecoveryDecision(RecoveryAction Action, string Reason);

/// <summary>
/// Narrow seam for the recoverer's GitHub call. Lets tests stub
/// PR creation without a real Octokit client.
/// </summary>
public interface IGitHubRecovery
{
    Task<PullRequest> CreatePullRequestAsync(
        string title, string body, string headBranch, string baseBranch, CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapter that wraps <see cref="GitHubService"/> for the
/// recoverer. Pulls only the surface the recoverer needs.
/// </summary>
public sealed class GitHubRecoveryAdapter : IGitHubRecovery
{
    private readonly GitHubService _gitHub;
    public GitHubRecoveryAdapter(GitHubService gitHub) { _gitHub = gitHub; }
    public Task<PullRequest> CreatePullRequestAsync(
        string title, string body, string headBranch, string baseBranch, CancellationToken cancellationToken = default)
        => _gitHub.CreatePullRequestAsync(title, body, headBranch, baseBranch, cancellationToken);
}