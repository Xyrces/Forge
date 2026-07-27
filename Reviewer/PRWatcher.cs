using Microsoft.Extensions.Logging;
using Octokit;
using Forge.AgentTools;
using Forge.Core;
using Forge.Dashboard;
using static Octokit.CommitState;
using static Octokit.PullRequestReviewState;

namespace Forge.Reviewer;

public sealed class PRWatcher
{
    /// <summary>Grace window for a consumed rework round to move the
    /// PR head before the round is treated as stalled (no-op
    /// completion or a died run). Must exceed the agent run timeout
    /// (default 15m; operator-tuned via spawner.agentRunTimeoutMinutes,
    /// currently 30m) so a legitimate long run is never re-fired
    /// mid-flight. Coupled to that config — if the timeout is raised
    /// past 30m, raise this too.</summary>
    private static readonly TimeSpan ReworkRoundGrace = TimeSpan.FromMinutes(35);

    private readonly GitHubService _gitHub;
    private readonly GitWorktreeService _worktrees;
    private readonly IIssueStore _issues;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _reworkRoundGrace;
    private readonly ILogger<PRWatcher> _logger;
    private readonly StageGates? _gates;
    private readonly IDashboardEventBus _events;
    private readonly Forge.Core.TaskStateMachine? _lifecycle;
    private readonly AgentRunStore? _runs;
    private readonly Forge.Core.Workflow.WorkflowResolver? _workflow;

    public PRWatcher(
        GitHubService gitHub,
        GitWorktreeService worktrees,
        IIssueStore issues,
        TimeSpan pollInterval,
        TimeSpan staleAfter,
        IDashboardEventBus events,
        ILogger<PRWatcher> logger,
        StageGates? gates = null,
        TimeSpan? reworkRoundGrace = null,
        Forge.Core.TaskStateMachine? lifecycle = null,
        AgentRunStore? runs = null,
        Forge.Core.Workflow.WorkflowResolver? workflow = null)
    {
        _gitHub = gitHub;
        _worktrees = worktrees;
        _issues = issues;
        _pollInterval = pollInterval;
        _staleAfter = staleAfter;
        _reworkRoundGrace = reworkRoundGrace ?? ReworkRoundGrace;
        _events = events;
        _logger = logger;
        _gates = gates;
        _lifecycle = lifecycle;
        _runs = runs;
        _workflow = workflow;
    }

    /// <summary>
    /// Outcome of a single watch poll (<see cref="PollWatchOnceAsync"/>).
    /// </summary>
    public enum WatchPollOutcome
    {
        /// <summary>Nothing terminal; the watch stays Pending for the next sweep.</summary>
        Pending,
        /// <summary>PR was merged (externally or by us); task + watch completed.</summary>
        Merged,
        /// <summary>Reviewer requested changes; task + watch blocked.</summary>
        Blocked,
        /// <summary>CI failed; task + watch failed.</summary>
        CiFailed,
        /// <summary>Watch exceeded its stale window; task + watch failed.</summary>
        Stale,
        /// <summary>CI failed / changes requested — task requeued for a rework round (watch stays live).</summary>
        Reworking,
        /// <summary>Unrecoverable input (missing prNumber); watch failed.</summary>
        Error,
    }

    /// <summary>
    /// ONE poll of a watched PR: fetch state, evaluate CI + reviews,
    /// and apply any terminal transition (merged / changes-requested /
    /// CI-failed / stale). Returns without retrying when nothing is
    /// terminal yet — the caller (OrchestratorAgent's watch sweep)
    /// re-polls on its own slow cadence. This is the quota-friendly
    /// path: 3 API calls per watch per sweep, no internal loop. The
    /// stale window is anchored to the watch issue's CreatedAt, so it
    /// stays stable no matter which sweep picks the watch up.
    /// </summary>
    public async Task<WatchPollOutcome> PollWatchOnceAsync(
        IssueRecord watchTask,
        CancellationToken cancellationToken = default,
        Func<int, IReadOnlyList<PullRequestReviewState>>? reviewsOverride = null,
        Func<PullRequest, string>? headShaOverride = null,
        Func<PullRequest, bool?>? mergeableOverride = null)
    {
        var prText = watchTask.GetMetadata("prNumber");
        if (!int.TryParse(prText, out var prNumber))
        {
            _logger.LogError("Watch issue {Id} missing prNumber", watchTask.Id);
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Failed, "missing prNumber", ct: cancellationToken);
            return WatchPollOutcome.Error;
        }

        var taskId = watchTask.GetMetadata("taskId") ?? watchTask.Id;
        var branch = watchTask.GetMetadata("branch") ?? watchTask.Title;
        var worktreePath = watchTask.GetMetadata("worktreePath");

        // Orphan guard: a watch whose task is already terminal
        // (operator closeout, breaker, or external resolution) must
        // not drive the task — a CI-failure fire would transition a
        // CLOSED task back to Pending (resurrection). Close the
        // watch and skip. Observed live 2026-07-26: pr-watch-44
        // kept polling after task-161 + PR #34 were closed.
        var watchedTask = await _issues.GetAsync(taskId, cancellationToken);
        if (watchedTask is null || watchedTask.Status is IssueStatus.Closed or IssueStatus.Completed)
        {
            _logger.LogInformation(
                "PR watch {WatchId}: watched task {TaskId} is {Status} — closing watch",
                watchTask.Id, taskId, watchedTask?.Status.ToString() ?? "missing");
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Closed,
                $"watched task {taskId} is terminal", ct: cancellationToken);
            return WatchPollOutcome.Merged;
        }

        if (DateTime.UtcNow - watchTask.CreatedAt > _staleAfter)
        {
            _logger.LogWarning("PR #{PrNumber} timed out after {Minutes:F0} minutes", prNumber, _staleAfter.TotalMinutes);
            await _issues.TransitionAsync(taskId, IssueStatus.Failed, "pr-stale", ct: cancellationToken);
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Failed, "pr-stale", ct: cancellationToken);
            await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
            return WatchPollOutcome.Stale;
        }

        var pr = await _gitHub.GetPullRequestAsync(prNumber, cancellationToken);

        // Externally merged (operator merged by hand, or the
        // branch protection bot did): close the loop exactly as
        // if we had merged it ourselves. Without this check the
        // watch polls forever on CI+reviews of a dead PR.
        if (pr.Merged)
        {
            _logger.LogInformation("PR #{PrNumber} was merged externally; closing task {TaskId}", prNumber, taskId);
            await ReportAsync(Forge.Core.TaskEvent.ExternallyMerged);
            await _gitHub.DeleteBranchAsync(branch, cancellationToken);
            await _issues.TransitionAsync(taskId, IssueStatus.Completed, null, ct: cancellationToken);
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Completed, null, ct: cancellationToken);
            await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrMerged,
                taskId, $"PR #{prNumber} merged externally; task completed"));
            return WatchPollOutcome.Merged;
        }
        // P4 e2e-harness seam: tests can return the SHA
        // directly without going through Octokit's
        // PullRequest.Head (which has a private setter).
        var sha = headShaOverride is not null ? headShaOverride(pr) : pr.Head.Sha;

        // Phase 2 shadow authority: report observed events to the
        // lifecycle machine BEFORE the watcher's own transitions (the
        // machine derives the pre-transition state itself). Awaited —
        // never fire-and-forget. Best-effort: machine failures must
        // not break the watch loop.
        async Task ReportAsync(Forge.Core.TaskEvent evt)
            => await ReportLifecycleAsync(watchTask, taskId, evt, cancellationToken);

        // Workflow policies (editable workflow, pass 3): resolved per
        // poll — a publish lands here on the very next sweep, no
        // restart. Fallbacks are the code constants (pre-definition
        // behavior); tests pass no resolver and keep ctor values.
        var wf = _workflow is not null ? await _workflow.ResolveAsync(cancellationToken) : null;
        var maxStrikes = wf is not null
            ? Forge.Core.Workflow.WorkflowPolicyReader.GetInt(wf, Forge.Core.Workflow.WorkflowPolicies.MaxStrikes, MaxReworkAttempts)
            : MaxReworkAttempts;
        var grace = wf is not null
            ? TimeSpan.FromMinutes(Forge.Core.Workflow.WorkflowPolicyReader.GetInt(
                wf, Forge.Core.Workflow.WorkflowPolicies.StallGraceMinutes, (int)_reworkRoundGrace.TotalMinutes))
            : _reworkRoundGrace;
        var parkOnInfra = wf is null
            || Forge.Core.Workflow.WorkflowPolicyReader.GetBool(wf, Forge.Core.Workflow.WorkflowPolicies.ParkOnInfra, true);
        var autoMerge = wf is null
            || Forge.Core.Workflow.WorkflowPolicyReader.GetBool(wf, Forge.Core.Workflow.WorkflowPolicies.AutoMerge, true);

        var ci = await _gitHub.GetCommitStatusAsync(sha, cancellationToken);
        // P4 e2e-harness seam: tests can pre-approve reviews
        // without going through Octokit's sealed
        // PullRequestReview ctor. Default = real GitHub call.
        // Formal GitHub reviews count as an OPERATOR verdict
        // (solo-identity: the operator approves via the GitHub UI) —
        // but only against the CURRENT head SHA: a review submitted
        // before a rework push is stale and must neither merge nor
        // block the new head (the same SHA rule the reviewer-agent
        // verdict below already follows). The reviewsOverride test
        // seam supplies states directly and is treated as current-head.
        var reviewStates = reviewsOverride is not null
            ? reviewsOverride(prNumber)
            : (await _gitHub.GetReviewsAsync(prNumber, cancellationToken))
                .Where(r => string.Equals(r.CommitId, sha, StringComparison.Ordinal))
                .Select(r => r.State.Value).ToList();

        // Reviewer-agent verdict from the watch's own metadata
        // (ReviewerDispatcher records reviewSha/reviewVerdict/
        // reviewNotes). Only counts against the CURRENT head SHA —
        // a stale verdict from a prior head is ignored.
        var agentVerdict = watchTask.GetMetadata("reviewVerdict");
        var agentVerdictSha = watchTask.GetMetadata("reviewSha");
        var agentVerdictCurrent = !string.IsNullOrEmpty(agentVerdict)
            && string.Equals(agentVerdictSha, sha, StringComparison.Ordinal);

        var operatorApproved = reviewStates.Any(s => s == PullRequestReviewState.Approved);
        var operatorChangesRequested = reviewStates.Any(s => s == PullRequestReviewState.ChangesRequested);
        var approved = operatorApproved
            || (agentVerdictCurrent && string.Equals(agentVerdict, nameof(ReviewerVerdict.Approve), StringComparison.Ordinal));
        var changesRequested = operatorChangesRequested
            || (agentVerdictCurrent && string.Equals(agentVerdict, nameof(ReviewerVerdict.RequestChanges), StringComparison.Ordinal));
        var reviewErrored = agentVerdictCurrent && string.Equals(agentVerdict, nameof(ReviewerVerdict.Error), StringComparison.Ordinal);

        var ciGreen = ci == CommitState.Success;
        var ciFailed = ci is CommitState.Failure or CommitState.Error;

        // Rework guard: a rework round was already queued FOR THIS
        // HEAD. The guard reads the MACHINE's record on the task
        // (reworkForSha + stateEnteredAt, written by
        // ReworkOrTripAsync's ReworkFired report). A round that's
        // queued (task Pending) or claimed inside the grace window
        // blocks re-fire; a claimed round past grace is stalled
        // (no-op or died run) and falls through to fire another
        // strike.
        {
            var guardTask = await _issues.GetAsync(taskId, cancellationToken);
            var recordedSha = guardTask?.GetMetadata("reworkForSha");
            if (string.Equals(recordedSha, sha, StringComparison.Ordinal))
            {
                // Run-registry first: an active dev run for this task
                // means the round is definitionally not stalled —
                // regardless of how long the queue + run have taken
                // (observed live 2026-07-27: the clock measured from
                // rework-fire time and could re-fire against a
                // healthy 30-min run).
                if (_runs is not null)
                {
                    var activeRun = (await _runs.ListActiveAsync(cancellationToken))
                        .Any(r => string.Equals(r.TaskId, taskId, StringComparison.Ordinal)
                            && r.Role is "CoreDev" or "ClientDev");
                    if (activeRun)
                    {
                        return WatchPollOutcome.Pending;
                    }
                }
                var enteredRaw = guardTask?.GetMetadata("stateEnteredAt");
                var enteredAt = DateTimeOffset.TryParse(enteredRaw, out var ts)
                    ? ts.UtcDateTime : guardTask?.UpdatedAt ?? DateTime.MinValue;
                var untouchedFor = DateTime.UtcNow - enteredAt;
                var roundStalled = guardTask is not null
                    && guardTask.Status == IssueStatus.InProgress
                    && untouchedFor > grace;
                if (!roundStalled)
                {
                    return WatchPollOutcome.Pending;
                }
                await ReportAsync(Forge.Core.TaskEvent.StallDetected);
                _logger.LogWarning(
                    "PR watch {WatchId}: rework round for {TaskId} stalled — no push and no task update for {Minutes:F0}m (head still {Sha}); re-firing as another strike",
                    watchTask.Id, taskId, untouchedFor.TotalMinutes, sha);
            }
        }

        _logger.LogDebug(
            "PR #{PrNumber}: CI={Ci} approved={Approved} changesRequested={Changes} reviewError={Err} (agent verdict={V}@{VS}, head={Head})",
            prNumber, ci, approved, changesRequested, reviewErrored, agentVerdict, agentVerdictSha, sha);

        // 1. All gates green -> merge. (External-merge handled above.)
        if (ciGreen && approved && !changesRequested)
        {
            await ReportAsync(Forge.Core.TaskEvent.CiGreen);
            // Green + approved but CONFLICTING: the base moved after
            // the approval (a sibling PR merged) and Octokit's merge
            // 405/409s into a false return. Without this branch the
            // watch retried the doomed merge every sweep forever —
            // observed live 2026-07-26: PRs #42/#43 spun 8+ hours,
            // sprint unable to complete. Route to the conflict sync
            // round instead. Mergeable==null means "still computing"
            // — attempt the merge and re-check on failure.
            var mergeableGreen = mergeableOverride?.Invoke(pr) ?? pr.Mergeable;
            if (mergeableGreen == false)
            {
                await ReportAsync(Forge.Core.TaskEvent.ConflictDetected);
                return await ReworkOrTripAsync(
                    watchTask, taskId, worktreePath, sha,
                    reason: "PR conflicts with the base branch",
                    context: ConflictContext,
                    terminalStatus: IssueStatus.Blocked,
                    terminalError: "PR conflicts with base branch (circuit breaker tripped after max rework attempts)",
                    terminalOutcome: WatchPollOutcome.Blocked,
                    cancellationToken,
                    maxStrikes: maxStrikes);
            }

            // autoMerge=false (workflow policy): same shape as a held
            // merge gate — the watch stays live, nothing fails, the
            // operator merges by hand (external merges detected above).
            if (!autoMerge)
            {
                _logger.LogInformation("PR #{PrNumber}: green + approved but autoMerge=false in the workflow definition — leaving for operator merge", prNumber);
                return WatchPollOutcome.Pending;
            }

            // Operator merge gate: hold auto-merge without failing
            // anything — the watch stays live and the next sweep
            // re-evaluates (external merges are still detected).
            if (_gates is not null && await _gates.IsHeldAsync(StageGates.Merge, cancellationToken))
            {
                _logger.LogInformation("PR #{PrNumber}: merge held by operator gate", prNumber);
                return WatchPollOutcome.Pending;
            }
            var merged = await _gitHub.MergePullRequestAsync(prNumber, cancellationToken);
            if (merged)
            {
                await ReportAsync(Forge.Core.TaskEvent.Merged);
                await _gitHub.DeleteBranchAsync(branch, cancellationToken);
                await _issues.TransitionAsync(taskId, IssueStatus.Completed, null, ct: cancellationToken);
                await _issues.TransitionAsync(watchTask.Id, IssueStatus.Completed, null, ct: cancellationToken);
                await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
                _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrMerged,
                    taskId, $"PR #{prNumber} merged and branch deleted"));
                _logger.LogInformation("PR #{PrNumber} merged; task {TaskId} completed", prNumber, taskId);
                return WatchPollOutcome.Merged;
            }
            // Merge refused with mergeable previously unknown: the
            // computation may have landed by now — a conflict gets
            // the sync round, anything else retries next sweep.
            var mergeableAfter = mergeableOverride?.Invoke(pr) ?? pr.Mergeable;
            if (mergeableAfter == false)
            {
                await ReportAsync(Forge.Core.TaskEvent.ConflictDetected);
                return await ReworkOrTripAsync(
                    watchTask, taskId, worktreePath, sha,
                    reason: "PR conflicts with the base branch",
                    context: ConflictContext,
                    terminalStatus: IssueStatus.Blocked,
                    terminalError: "PR conflicts with base branch (circuit breaker tripped after max rework attempts)",
                    terminalOutcome: WatchPollOutcome.Blocked,
                    cancellationToken,
                    maxStrikes: maxStrikes);
            }
            _logger.LogWarning("PR #{PrNumber} merge returned false; will retry next poll", prNumber);
            return WatchPollOutcome.Pending;
        }

        // 2. Gates failed -> rework loop with a circuit breaker.
        //    The engineer gets the failure context and pushes to the
        //    SAME branch/PR; the watch stays live. After
        //    MaxReworkAttempts the circuit opens: terminal state for
        //    the operator.
        if (ciFailed)
        {
            // Pre-existing-failure park (observed live 2026-07-25:
            // the e2e harness broke on main and EVERY PR burned 3
            // doomed rework rounds each before tripping the breaker
            // — 18 rounds of pure token waste). A check that is also
            // red on the base branch's own head is an infra failure,
            // not this PR's: park the watch (no strike, no rework)
            // until the base goes green, then fire ONE no-strike
            // refresh round so the PR retriggers CI on a fresh head.
            // Phase 3: the parked record lives on the TASK via the
            // machine (state=ParkedInfra + parkedForSha); the legacy
            // watch flag is the migration fallback.
            var baseBranch = pr.Base?.Ref ?? "main";
            var parkTask = await _issues.GetAsync(taskId, cancellationToken);
            var parkedSha = parkTask is not null
                && string.Equals(parkTask.GetMetadata("state"), nameof(Forge.Core.TaskLifecycleState.ParkedInfra), StringComparison.Ordinal)
                ? parkTask.GetMetadata("parkedForSha")
                : null;
            if (parkOnInfra && string.Equals(parkedSha, sha, StringComparison.Ordinal))
            {
                var baseHead = await _gitHub.GetBranchHeadShaAsync(baseBranch, cancellationToken);
                var baseCi = await _gitHub.GetCommitStatusAsync(baseHead, cancellationToken);
                if (baseCi is CommitState.Failure or CommitState.Error)
                {
                    return WatchPollOutcome.Pending;   // still parked; infra still red
                }
                // Base recovered: fire the refresh round (no strike).
                // The ReworkFired report below overwrites the parked
                // state on the machine record.
                await ReportAsync(Forge.Core.TaskEvent.BaseRecovered);
                return await ReworkOrTripAsync(
                    watchTask, taskId, worktreePath, sha,
                    reason: "base-branch CI recovered — retrigger PR CI",
                    context: $"The failing CI on this PR was pre-existing breakage on the base branch ({baseBranch}), not caused by your diff — the base is green again now. Bring your branch up to date so CI retriggers: git fetch origin && git merge origin/{baseBranch}, run the full test suite, and push to the SAME branch. Do not restructure your earlier work.",
                    terminalStatus: IssueStatus.Failed,
                    terminalError: "post-recovery refresh rounds exhausted",
                    terminalOutcome: WatchPollOutcome.CiFailed,
                    cancellationToken,
                    countAsStrike: false,
                    maxStrikes: maxStrikes);
            }
            if (parkOnInfra && !string.Equals(parkedSha, sha, StringComparison.Ordinal))
            {
                var baseHead = await _gitHub.GetBranchHeadShaAsync(baseBranch, cancellationToken);
                var baseCi = await _gitHub.GetCommitStatusAsync(baseHead, cancellationToken);
                if (baseCi is CommitState.Failure or CommitState.Error)
                {
                    _logger.LogWarning(
                        "PR #{PrNumber}: CI failure is pre-existing on {Base} (base head {Sha} also red) — parking watch without a strike until the base recovers",
                        prNumber, baseBranch, baseHead);
                    // The machine records the park on the task
                    // (state=ParkedInfra + parkedForSha) — no watch
                    // flag write anymore.
                    await ReportLifecycleAsync(watchTask, taskId, Forge.Core.TaskEvent.ParkedOnInfra, cancellationToken,
                        extraMetadata: new Dictionary<string, object> { ["parkedForSha"] = sha });
                    _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
                        taskId, $"Watch parked: CI failure is pre-existing on {baseBranch}; merge pipeline paused until base recovers"));
                    return WatchPollOutcome.Pending;
                }
            }
            // Genuine PR failure (base is green): strike, with the
            // failing check-run details so the rework agent doesn't
            // have to guess what broke.
            await ReportAsync(Forge.Core.TaskEvent.CiRedOnPr);
            var failing = await _gitHub.GetFailedCheckRunSummariesAsync(sha, cancellationToken);
            var detail = failing.Count > 0
                ? " Failing checks:\n" + string.Join("\n", failing.Select(f => $"- {f}"))
                : "";
            return await ReworkOrTripAsync(
                watchTask, taskId, worktreePath, sha,
                reason: $"CI failed for {sha[..Math.Min(7, sha.Length)]}: {ci}",
                context: $"CI checks failed ({ci}). Fix the build/tests on the same branch.{detail}",
                terminalStatus: IssueStatus.Failed,
                terminalError: $"CI failed after max rework attempts: {ci}",
                terminalOutcome: WatchPollOutcome.CiFailed,
                cancellationToken,
                maxStrikes: maxStrikes);
        }
        if (changesRequested)
        {
            await ReportAsync(Forge.Core.TaskEvent.ReviewChangesRequested);
            var notes = watchTask.GetMetadata("reviewNotes");
            return await ReworkOrTripAsync(
                watchTask, taskId, worktreePath, sha,
                reason: "reviewer requested changes",
                context: $"The reviewer requested changes:\n{notes}",
                terminalStatus: IssueStatus.Blocked,
                terminalError: "changes-requested (circuit breaker tripped after max rework attempts)",
                terminalOutcome: WatchPollOutcome.Blocked,
                cancellationToken,
                maxStrikes: maxStrikes);
        }
        if (reviewErrored)
        {
            // Reviewer agent itself is failing (LLM outage, parse
            // errors). Circuit-breaker on review rounds, then the
            // operator must review by hand.
            var rounds = int.TryParse(watchTask.GetMetadata("reviewRound"), out var r) ? r : 1;
            if (rounds >= maxStrikes)
            {
                _logger.LogWarning("PR #{PrNumber}: reviewer unavailable after {Rounds} rounds; blocking for operator review", prNumber, rounds);
                await _issues.TransitionAsync(taskId, IssueStatus.Blocked, "reviewer unavailable — operator review required", ct: cancellationToken);
                await _issues.TransitionAsync(watchTask.Id, IssueStatus.Blocked, "reviewer-error", ct: cancellationToken);
                return WatchPollOutcome.Blocked;
            }
            return WatchPollOutcome.Pending;
        }

        // 2b. The PR conflicts with the base branch. GitHub refuses
        //     the merge AND creates no pull_request CI runs for a
        //     conflicting PR (the test merge ref can't be built), so
        //     waiting on CI is futile — observed live 2026-07-25:
        //     PR #33 sat approved-but-unmergeable with zero CI runs
        //     and an operator merged main by hand. Operator rule
        //     (same day): the loop must handle this, not a human.
        //     Sync rework round: merge main into the SAME branch,
        //     resolve conflicts, push. The head moves, CI runs, the
        //     reviewer re-reviews, and the merge gate re-evaluates.
        //     Mergeable is null while GitHub computes — skip those
        //     sweeps rather than firing spurious rounds.
        var mergeable = mergeableOverride?.Invoke(pr) ?? pr.Mergeable;
        if (mergeable == false)
        {
            await ReportAsync(Forge.Core.TaskEvent.ConflictDetected);
            return await ReworkOrTripAsync(
                watchTask, taskId, worktreePath, sha,
                reason: "PR conflicts with the base branch",
                context: ConflictContext,
                terminalStatus: IssueStatus.Blocked,
                terminalError: "PR conflicts with base branch (circuit breaker tripped after max rework attempts)",
                terminalOutcome: WatchPollOutcome.Blocked,
                cancellationToken,
                maxStrikes: maxStrikes);
        }

        // 3. Otherwise: CI pending, or review for the current head
        //    hasn't landed yet. Keep polling.
        return WatchPollOutcome.Pending;
    }

    /// <summary>
    /// Circuit-breaker constant: maximum rework rounds (CI failures
    /// + reviewer change requests share one counter) before the task
    /// goes terminal for the operator.
    /// </summary>
    public const int MaxReworkAttempts = 3;

    /// <summary>Shared context for conflict sync rounds (used by the
    /// green+approved-conflicting route and the non-green conflict
    /// check).</summary>
    private const string ConflictContext =
        "The PR branch has merge conflicts with the base branch and cannot be merged — GitHub does not even run CI on a conflicting PR. Merge the base branch into your branch (git fetch origin && git merge origin/main), resolve the conflicts minimally, run the full test suite, and push to the SAME branch. Keep your earlier changes intact; do not restructure unrelated work.";

    /// <summary>Phase 2 shadow-authority helper: report an observed
    /// event to the lifecycle machine. Best-effort — never breaks the
    /// watch loop.</summary>
    private async Task ReportLifecycleAsync(
        IssueRecord watchTask, string taskId, Forge.Core.TaskEvent evt, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object>? extraMetadata = null)
    {
        if (_lifecycle is null) return;
        try
        {
            var task = await _issues.GetAsync(taskId, cancellationToken);
            if (task is not null)
            {
                await _lifecycle.ReportAsync(task, evt, watchTask, hasActiveDevRun: false, cancellationToken, extraMetadata);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lifecycle report {Event} failed for {TaskId}; continuing", evt, taskId);
        }
    }

    /// <summary>
    /// Requeue the task for a rework round (Pending, with the failure
    /// context the engineer's prompt will surface), keeping the watch
    /// live and the worktree in place (the agent continues on the
    /// same branch). When the circuit breaker trips, fall back to the
    /// terminal transition instead.
    /// </summary>
    private async Task<WatchPollOutcome> ReworkOrTripAsync(
        IssueRecord watchTask,
        string taskId,
        string? worktreePath,
        string headSha,
        string reason,
        string context,
        IssueStatus terminalStatus,
        string terminalError,
        WatchPollOutcome terminalOutcome,
        CancellationToken cancellationToken,
        bool countAsStrike = true,
        int? maxStrikes = null)
    {
        var task = await _issues.GetAsync(taskId, cancellationToken);
        var attempts = 0;
        if (task is not null)
        {
            var raw = task.GetMetadata("reworkAttempts");
            if (raw is not null) int.TryParse(raw, out attempts);
        }

        if (task is null || (countAsStrike && attempts >= (maxStrikes ?? MaxReworkAttempts)))
        {
            _logger.LogWarning("PR watch {WatchId}: circuit breaker tripped for {TaskId} after {N} rework attempts ({Reason})",
                watchTask.Id, taskId, attempts, reason);
            await ReportLifecycleAsync(watchTask, taskId, Forge.Core.TaskEvent.BreakerTripped, cancellationToken);
            await _issues.TransitionAsync(taskId, terminalStatus, terminalError, ct: cancellationToken);
            await _issues.TransitionAsync(watchTask.Id, terminalStatus, terminalError, ct: cancellationToken);
            if (terminalStatus == IssueStatus.Failed)
            {
                await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
            }
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrFailed,
                taskId, $"Circuit breaker: {reason} ({attempts} rework attempts)"));
            return terminalOutcome;
        }

        // Rework round: task back to Pending (it remains a sprint
        // member, so the dispatch gate passes it again), failure
        // context in metadata for the agent prompt, watch untouched.
        // countAsStrike=false is the post-infra-recovery refresh
        // round: it must not consume breaker budget.
        if (countAsStrike) attempts++;
        _logger.LogInformation(
            "PR watch {WatchId}: rework round {N}/{Max} for {TaskId} — {Reason}",
            watchTask.Id, attempts, MaxReworkAttempts, taskId, reason);
        await ReportLifecycleAsync(watchTask, taskId, Forge.Core.TaskEvent.ReworkFired, cancellationToken,
            extraMetadata: new Dictionary<string, object> { ["reworkForSha"] = headSha });
        var metadata = ParseMetadataDict(task.MetadataJson);
        metadata["reworkAttempts"] = attempts.ToString();
        metadata["reworkReason"] = reason;
        metadata["reworkContext"] = context.Length > 3000 ? context[..3000] : context;
        await _issues.TransitionAsync(taskId, IssueStatus.Pending, error: null, metadata: metadata, ct: cancellationToken);

        // The round record lives on the TASK via the machine
        // (state=ReworkQueued + reworkForSha, written by the
        // ReworkFired report above). No watch-flag write — the guard
        // reads the machine record now (legacy reworkInFlightSha on
        // old watches remains as the migration fallback).

        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
            taskId, $"Rework round {attempts}/{MaxReworkAttempts}: {reason}"));
        return WatchPollOutcome.Reworking;
    }

    private static Dictionary<string, object> ParseMetadataDict(string? metadataJson)
    {
        var metadata = new Dictionary<string, object>();
        if (string.IsNullOrWhiteSpace(metadataJson)) return metadata;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    // JSON null is the delete idiom: a cleared key is
                    // ABSENT, not the literal string "null" (GetRawText
                    // would resurrect it as one on the next write).
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                    metadata[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? p.Value.GetString()! : p.Value.GetRawText();
                }
            }
        }
        catch { /* malformed metadata: start fresh */ }
        return metadata;
    }

    public async Task<int> ProcessWatchTaskAsync(
        IssueRecord watchTask,
        CancellationToken cancellationToken = default,
        Func<int, IReadOnlyList<PullRequestReviewState>>? reviewsOverride = null,
        Func<PullRequest, string>? headShaOverride = null)
    {
        var prText = watchTask.GetMetadata("prNumber");
        if (!int.TryParse(prText, out _))
        {
            _logger.LogError("Watch issue {Id} missing prNumber", watchTask.Id);
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Failed, "missing prNumber", ct: cancellationToken);
            return 1;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await PollWatchOnceAsync(
                watchTask, cancellationToken, reviewsOverride, headShaOverride);
            switch (outcome)
            {
                case WatchPollOutcome.Merged:
                case WatchPollOutcome.Blocked:
                    return 0;
                case WatchPollOutcome.Reworking:
                    // Rework round queued; the loop's job is done for
                    // now — the sweep (or a future harness invocation)
                    // resumes polling once the head moves.
                    return 0;
                case WatchPollOutcome.CiFailed:
                case WatchPollOutcome.Error:
                    return 1;
                case WatchPollOutcome.Stale:
                    return 124;
                case WatchPollOutcome.Pending:
                default:
                    break;
            }

            try { await Task.Delay(_pollInterval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }

        return 0;
    }

    public static ReviewVerdict EvaluateVerdict(CommitState ci, IReadOnlyList<PullRequestReview> reviews)
        => EvaluateVerdictFromStates(ci, reviews.Select(r => r.State.Value).ToList());

    public static ReviewVerdict EvaluateVerdictFromStates(CommitState ci, IReadOnlyCollection<PullRequestReviewState> reviewStates)
    {
        if (ci == CommitState.Failure || ci == CommitState.Error)
            return ReviewVerdict.CiFailed;

        var hasApproved = reviewStates.Any(s => s == Approved);
        var hasChanges = reviewStates.Any(s => s == ChangesRequested);

        return (ci, hasApproved, hasChanges) switch
        {
            (Success, true, _) => ReviewVerdict.GreenAndApproved,
            (Success, _, true) => ReviewVerdict.GreenChangesRequested,
            _ => ReviewVerdict.Pending
        };
    }

    private async Task TryRemoveWorktreeAsync(string? worktreePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(worktreePath)) return;
        try
        {
            var taskId = Path.GetFileName(worktreePath);
            await _worktrees.RemoveAsync(taskId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove worktree {Path}", worktreePath);
        }
    }
}

public enum ReviewVerdict
{
    Pending,
    GreenAndApproved,
    GreenChangesRequested,
    CiFailed
}
