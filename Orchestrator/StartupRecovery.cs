using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Octokit;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;

namespace PortHorizon.Agents.Orchestrator;

/// <summary>
/// P4 Stage A — checkpoint-based recovery. Runs at startup
/// BEFORE the dispatch loop. See <c>docs/p4-restart-safety.md</c>.
///
/// <para>
/// For every issue in <c>status=InProgress + assignee=kilo</c>:
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
        StartupRecoveryOptions? options = null)
    {
        _issues = issues;
        _reports = reports;
        _worktrees = worktrees;
        _gitHub = gitHub;
        _events = events;
        _logger = logger;
        _options = options ?? new StartupRecoveryOptions();
    }

    public StartupRecoveryOptions Options => _options;

    /// <summary>
    /// Sweep every in-progress kilo issue. Returns the
    /// <see cref="RecoveryReportRecord"/> id of the audit row.
    /// </summary>
    public async Task<long> RunAsync(string? specId = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var report = await _reports.StartAsync(specId, ct);
        _logger.LogInformation("StartupRecovery: starting report {Id} (specId={Spec})", report.Id, specId ?? "<all>");

        var scanned = 0;
        var replayed = 0;
        var failed = 0;
        var actions = new List<RecoveryActionRecord>();
        try
        {
            var candidates = await _issues.ListInProgressForRecoveryAsync(ct);
            scanned = candidates.Count;
            foreach (var issue in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (specId is not null && issue.GetMetadata("specId") != specId) continue;
                var action = await ReplayAsync(issue, ct);
                actions.Add(action);
                if (action.Action == "replay") replayed++;
                else if (action.Action == "failed") failed++;
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
    /// Inspect one issue and replay the next side-effect.
    /// Returns a <see cref="RecoveryActionRecord"/> describing
    /// what the recoverer did.
    /// </summary>
    public async Task<RecoveryActionRecord> ReplayAsync(IssueRecord issue, CancellationToken ct = default)
    {
        var before = issue.DispatchCheckpoint?.ToDbValue();
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
                    await _issues.IncrementRecoveryAttemptsAsync(issue.Id, ct);
                    if (issue.RecoveryAttempts + 1 >= _options.MaxAttempts)
                    {
                        // Hard fail: clean up worktree + transition to Failed.
                        await TryRemoveWorktreeAsync(issue, ct);
                        await _issues.TransitionAsync(issue.Id, IssueStatus.Failed,
                            $"recovered: {decision.Reason}", metadata: new Dictionary<string, object>
                            {
                                ["lastError"] = $"recovered: {decision.Reason}",
                                ["recoveryFailedAt"] = DateTime.UtcNow.ToString(IssueStore.DateFormat),
                            }, ct: ct);
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
                    return await ReplayFromCheckpointAsync(issue, decision, ct);

                default:
                    return new RecoveryActionRecord(issue.Id, before, before, "left_alone",
                        $"unknown decision {decision.Action}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery({Id}): crashed", issue.Id);
            return new RecoveryActionRecord(issue.Id, before, before, "failed", ex.Message);
        }
    }

    private async Task<RecoveryActionRecord> ReplayFromCheckpointAsync(
        IssueRecord issue, RecoveryDecision decision, CancellationToken ct)
    {
        var before = issue.DispatchCheckpoint?.ToDbValue();
        var worktreePath = issue.GetMetadata("worktreePath");
        var branch = issue.GetMetadata("branch") ?? $"agent/{issue.Id}";
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            // The recoverer can't replay without the worktree. Fail it.
            await _issues.TransitionAsync(issue.Id, IssueStatus.Failed,
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
                    // The cheapest correct thing is: leave it in
                    // InProgress; the next dispatch tick will
                    // re-claim it and re-run from scratch.
                    // The worktree already exists, so re-acquire
                    // is a no-op.
                    _events.Publish(RecoveryEvent(issue.Id, "left_alone",
                        "worktree_acquired; LLM re-run deferred to next dispatch tick"));
                    return new RecoveryActionRecord(issue.Id, before, before, "left_alone",
                        "worktree_acquired; LLM re-run deferred to next dispatch tick");

                case DispatchCheckpoint.AgentCompleted:
                    // Commit + push + PR.
                    await CommitAndPushAsync(issue, worktreePath!, branch, ct);
                    await TryOpenPrAsync(issue, branch, ct);
                    await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PrOpened, ct);
                    await _issues.IncrementRecoveryAttemptsAsync(issue.Id, ct);
                    _events.Publish(RecoveryEvent(issue.Id, "replay", "agent_completed -> pr_opened"));
                    return new RecoveryActionRecord(issue.Id, before, DispatchCheckpoint.PrOpened.ToDbValue(), "replay", null);

                case DispatchCheckpoint.CommitDone:
                    await _worktrees.PushAsync(worktreePath!, branch, ct);
                    await TryOpenPrAsync(issue, branch, ct);
                    await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PrOpened, ct);
                    await _issues.IncrementRecoveryAttemptsAsync(issue.Id, ct);
                    _events.Publish(RecoveryEvent(issue.Id, "replay", "commit_done -> pr_opened"));
                    return new RecoveryActionRecord(issue.Id, before, DispatchCheckpoint.PrOpened.ToDbValue(), "replay", null);

                case DispatchCheckpoint.PushDone:
                    await TryOpenPrAsync(issue, branch, ct);
                    await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PrOpened, ct);
                    await _issues.IncrementRecoveryAttemptsAsync(issue.Id, ct);
                    _events.Publish(RecoveryEvent(issue.Id, "replay", "push_done -> pr_opened"));
                    return new RecoveryActionRecord(issue.Id, before, DispatchCheckpoint.PrOpened.ToDbValue(), "replay", null);

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

        // Already has a PR — leave alone. The watch path is
        // independent of the workflow.
        if (!string.IsNullOrEmpty(prNumber) && int.TryParse(prNumber, out _))
        {
            return new RecoveryDecision(RecoveryAction.LeftAlone, "prNumber already recorded");
        }

        // No checkpoint recorded (legacy issue from before v11) —
        // treat as claimed; the next dispatch tick will re-acquire
        // (idempotent).
        if (issue.DispatchCheckpoint is null)
        {
            return new RecoveryDecision(RecoveryAction.LeftAlone,
                "no checkpoint recorded (pre-v11 issue); dispatch loop will re-pick");
        }

        switch (issue.DispatchCheckpoint)
        {
            case DispatchCheckpoint.Claimed:
                return new RecoveryDecision(RecoveryAction.LeftAlone,
                    "just-claimed; dispatch loop will pick up on next tick");

            case DispatchCheckpoint.WorktreeAcquired:
                if (!worktreeExists)
                    return new RecoveryDecision(RecoveryAction.Failed,
                        "worktree_acquired but directory missing");
                return new RecoveryDecision(RecoveryAction.LeftAlone,
                    "worktree exists; LLM re-run deferred to dispatch loop");

            case DispatchCheckpoint.AgentCompleted:
            case DispatchCheckpoint.CommitDone:
            case DispatchCheckpoint.PushDone:
                if (!worktreeExists)
                    return new RecoveryDecision(RecoveryAction.Failed,
                        $"{issue.DispatchCheckpoint} but worktree missing at {worktreePath}");
                return new RecoveryDecision(RecoveryAction.Replay,
                    $"replay from {issue.DispatchCheckpoint}");

            case DispatchCheckpoint.PrOpened:
                // pr_opened but no prNumber — odd, leave alone and
                // let the dispatch loop figure it out.
                return new RecoveryDecision(RecoveryAction.LeftAlone,
                    "pr_opened checkpoint but no prNumber; dispatch loop will re-handle");

            default:
                return new RecoveryDecision(RecoveryAction.Failed, $"unknown checkpoint {issue.DispatchCheckpoint}");
        }
    }

    private async Task CommitAndPushAsync(
        IssueRecord issue, string worktreePath, string branch, CancellationToken ct)
    {
        var commit = await _worktrees.CommitAllAsync(
            worktreePath, $"Task({issue.Id}): {issue.Title}", ct);
        if (!commit.HasChanges)
        {
            _logger.LogInformation("Recovery({Id}): no changes to commit on {Branch}", issue.Id, branch);
        }
        else
        {
            _logger.LogInformation("Recovery({Id}): committed on {Branch}", issue.Id, branch);
        }
        await _worktrees.PushAsync(worktreePath, branch, ct);
        var headSha = await _worktrees.GetHeadShaAsync(worktreePath, ct);
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.CommitDone, ct);
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PushDone, ct);
        // Record branchSha so the dispatch loop's PR step can pick it up.
        await _issues.TransitionAsync(issue.Id, issue.Status, error: null,
            metadata: new Dictionary<string, object> { ["branchSha"] = headSha }, ct: ct);
    }

    private async Task TryOpenPrAsync(IssueRecord issue, string branch, CancellationToken ct)
    {
        var pr = await _gitHub.CreatePullRequestAsync(
            title: $"[{issue.Type}] {issue.Title}",
            body: $"Task: {issue.Id}\n\n(recovered by StartupRecovery after a crash)\n",
            headBranch: branch,
            baseBranch: "main",
            cancellationToken: ct);
        await _issues.TransitionAsync(issue.Id, issue.Status, error: null,
            metadata: new Dictionary<string, object> { ["prNumber"] = pr.Number }, ct: ct);
        _logger.LogInformation("Recovery({Id}): opened PR #{Pr}", issue.Id, pr.Number);
    }

    private async Task TryRemoveWorktreeAsync(IssueRecord issue, CancellationToken ct)
    {
        try { await _worktrees.RemoveAsync(issue.Id, ct); }
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