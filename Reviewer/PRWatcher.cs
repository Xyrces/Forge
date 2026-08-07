using Microsoft.Extensions.Logging;
using Octokit;
using Forge.AgentTools;
using Forge.Core;
using Forge.Core.Workflow;
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

    /// <summary>True when the lifecycle machine is wired — exposed
    /// for composition-wiring tests. A watcher without it no-ops
    /// every report and silently disables the rework guard (observed
    /// live 2026-07-31).</summary>
    internal bool HasLifecycle => _lifecycle is not null;
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
    public async Task<WatchPollOutcome> PollWatchedTaskAsync(
        IssueRecord task,
        CancellationToken cancellationToken = default,
        Func<int, IReadOnlyList<PullRequestReviewState>>? reviewsOverride = null,
        Func<PullRequest, string>? headShaOverride = null,
        Func<PullRequest, bool?>? mergeableOverride = null)
    {
        var prText = task.GetMetadata("prNumber");
        if (!int.TryParse(prText, out var prNumber))
        {
            // Tasks enter the sweep BECAUSE they carry a prNumber —
            // reaching this means the row is corrupt. Log and skip;
            // never transition a dev task over a machinery problem.
            _logger.LogError("Watched task {Id} missing prNumber", task.Id);
            return WatchPollOutcome.Error;
        }

        var taskId = task.Id;
        var branch = task.GetMetadata("branch") ?? $"agent/{task.Id}";
        var worktreePath = task.GetMetadata("worktreePath");

        // Orphan guard (was the watch-closeout guard): a task that
        // went terminal between selection and poll (operator close,
        // external resolution) must not be driven — a CI-failure
        // fire would resurrect a CLOSED task to Pending. Re-read:
        // the caller's record can be stale by minutes.
        var freshTask = await _issues.GetAsync(task.Id, cancellationToken) ?? task;
        if (freshTask.Status is IssueStatus.Closed or IssueStatus.Completed)
        {
            _logger.LogInformation(
                "Watched task {TaskId} is {Status} — nothing to watch",
                task.Id, freshTask.Status);
            return WatchPollOutcome.Merged;
        }

        // Stale window anchored to the PR-open time (prOpenedAt
        // metadata, written by CommitPushPrExecutor / recovery;
        // legacy rows fall back to task creation).
        var prOpenedRaw = task.GetMetadata("prOpenedAt");
        var anchor = DateTimeOffset.TryParse(prOpenedRaw, out var openedAt)
            ? openedAt.UtcDateTime : task.CreatedAt;
        if (DateTime.UtcNow - anchor > _staleAfter)
        {
            _logger.LogWarning("PR #{PrNumber} timed out after {Minutes:F0} minutes", prNumber, _staleAfter.TotalMinutes);
            await _issues.TransitionAsync(taskId, IssueStatus.Failed, "pr-stale", ct: cancellationToken);
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
            => await ReportLifecycleAsync(task, evt, cancellationToken);

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
        // Structural edits (pass 4): review step disabled = the
        // reviewer-agent verdict doesn't count (merge then requires a
        // formal review at the current head); the CI-red branch can
        // be switched from rework to block.
        var reviewEnabled = wf is null || wf.IsStepEnabled("review");
        var ciRedOutcome = wf?.GetEdgeSelection("pr", "rework", "rework") ?? "rework";

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
        var agentVerdict = task.GetMetadata("reviewVerdict");
        var agentVerdictSha = task.GetMetadata("reviewSha");
        var agentVerdictCurrent = reviewEnabled
            && !string.IsNullOrEmpty(agentVerdict)
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
                    "PR watch (task {TaskId}): rework round stalled — no push and no task update for {Minutes:F0}m (head still {Sha}); re-firing as another strike",
                    taskId, untouchedFor.TotalMinutes, sha);
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
                    task, worktreePath, sha,
                    reason: ConflictReworkReason,
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
                    task, worktreePath, sha,
                    reason: ConflictReworkReason,
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
                    task, worktreePath, sha,
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
                    await ReportLifecycleAsync(task, Forge.Core.TaskEvent.ParkedOnInfra, cancellationToken,
                        extraMetadata: new Dictionary<string, object> { ["parkedForSha"] = sha });
                    _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
                        taskId, $"Watch parked: CI failure is pre-existing on {baseBranch}; merge pipeline paused until base recovers"));
                    return WatchPollOutcome.Pending;
                }
            }
            // Genuine PR failure (base is green): strike, with the
            // failing check-run details so the rework agent doesn't
            // have to guess what broke. Branch option "block" (pass
            // 4): no rework round — straight to terminal Blocked.
            await ReportAsync(Forge.Core.TaskEvent.CiRedOnPr);
            if (string.Equals(ciRedOutcome, "block", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "PR #{PrNumber}: CI failed and the workflow's CI-red branch is 'block' — no rework round; task Blocked for the operator",
                    prNumber);
                await _issues.TransitionAsync(taskId, IssueStatus.Blocked,
                    $"CI failed ({ci}); workflow branch 'on CI failure' = block", ct: cancellationToken);
                _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrFailed,
                    taskId, $"CI failed ({ci}) — workflow branch set to block"));
                return WatchPollOutcome.Blocked;
            }
            var failing = await _gitHub.GetFailedCheckRunSummariesAsync(sha, cancellationToken);
            var detail = failing.Count > 0
                ? " Failing checks:\n" + string.Join("\n", failing.Select(f => $"- {f}"))
                : "";
            return await ReworkOrTripAsync(
                task, worktreePath, sha,
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
            var notes = task.GetMetadata("reviewNotes");
            return await ReworkOrTripAsync(
                task, worktreePath, sha,
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
            var rounds = int.TryParse(task.GetMetadata("reviewRound"), out var r) ? r : 1;
            if (rounds >= maxStrikes)
            {
                _logger.LogWarning("PR #{PrNumber}: reviewer unavailable after {Rounds} rounds; blocking until the reviewer model recovers", prNumber, rounds);
                // Transient block: the reviewer MODEL was unavailable
                // (LLM outage / rate-limit), the PR itself may be
                // fine. blockedKind marks the task for the dispatch
                // loop's auto-resume sweep — it re-reviews the head
                // once the model is back instead of waiting for an
                // operator. Genuine blocks (circuit breaker, CI-red
                // 'block', conflicts) carry no marker and stay
                // operator-decision.
                await _issues.TransitionAsync(taskId, IssueStatus.Blocked, "reviewer unavailable — will auto-resume when the reviewer model recovers",
                    new Dictionary<string, object> { ["blockedKind"] = BlockedKindReviewerUnavailable }, ct: cancellationToken);
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
                task, worktreePath, sha,
                reason: ConflictReworkReason,
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

    /// <summary><c>blockedKind</c> metadata value written when a task
    /// is Blocked solely because the reviewer model was unavailable
    /// (LLM outage / rate-limit). The dispatch loop's watch sweep
    /// auto-resumes tasks carrying this marker once the reviewer model
    /// is no longer cooling down; all other Blocked tasks remain
    /// operator-decision.</summary>
    public const string BlockedKindReviewerUnavailable = "reviewer-unavailable";

    /// <summary>Max auto-resume rounds for a blocked watch before
    /// the task falls back to operator-decision. Shared by the
    /// transient reviewer-unavailable resume and the
    /// mergeable-gate resume.</summary>
    public const int MaxAutoResumeAttempts = 3;

    /// <summary>The rework reason marking a conflict-sync round —
    /// the conflict mutex keys on it, so it must be a single
    /// literal.</summary>
    internal const string ConflictReworkReason = "PR conflicts with the base branch";

    /// <summary>Shared context for conflict sync rounds (used by the
    /// green+approved-conflicting route and the non-green conflict
    /// check).</summary>
    private const string ConflictContext =
        "The PR branch has merge conflicts with the base branch and cannot be merged — GitHub does not even run CI on a conflicting PR. Merge the base branch into your branch (git fetch origin && git merge origin/main), resolve the conflicts minimally, run the full test suite, and push to the SAME branch. Keep your earlier changes intact; do not restructure unrelated work.";

    /// <summary>
    /// External-fix recovery for a Blocked watched task (operator
    /// ask 2026-07-31: blocked tasks must self-heal when the world
    /// improves). Resume only when the merge gate would pass RIGHT
    /// NOW: PR open + mergeable + CI green at the head + an approval
    /// recorded at that head (reviewer-agent verdict or formal
    /// review). Genuine blocks whose condition has NOT cleared
    /// (conflicting, CI red, changes-requested at head) stay
    /// operator-decision. Shares the autoResumeAttempts budget with
    /// the transient reviewer-unavailable resume.
    /// Returns the resumed task, or null when the gate fails.
    /// </summary>
    public async Task<IssueRecord?> TryResumeMergeableBlockedAsync(
        IssueRecord task, CancellationToken cancellationToken = default,
        Func<PullRequest, string>? headShaOverride = null,
        Func<PullRequest, bool?>? mergeableOverride = null,
        Func<int, IEnumerable<PullRequestReviewState>>? reviewsOverride = null)
    {
        if (task.Status != IssueStatus.Blocked) return null;
        var prText = task.GetMetadata("prNumber");
        if (!int.TryParse(prText, out var prNumber)) return null;

        var attempts = int.TryParse(task.GetMetadata("autoResumeAttempts"), out var a) ? a : 0;
        if (attempts >= MaxAutoResumeAttempts)
        {
            _logger.LogDebug("Task {Id}: mergeable-resume budget exhausted; stays operator-decision", task.Id);
            return null;
        }

        PullRequest pr;
        try
        {
            pr = await _gitHub.GetPullRequestAsync(prNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Task {Id}: mergeable-resume PR fetch failed; skipping", task.Id);
            return null;
        }
        // Octokit fakes in tests leave State unset; a PR we just
        // fetched for a live watch is treated as open unless the API
        // says otherwise.
        try
        {
            if (pr.State.Value != ItemState.Open) return null;
        }
        catch (ArgumentException) { /* unset on test fakes — treat as open */ }
        var sha = headShaOverride?.Invoke(pr) ?? pr.Head.Sha;
        var mergeable = mergeableOverride?.Invoke(pr) ?? pr.Mergeable;
        if (mergeable != true) return null;

        CommitState ci;
        try
        {
            ci = await _gitHub.GetCommitStatusAsync(sha, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Task {Id}: mergeable-resume CI fetch failed; skipping", task.Id);
            return null;
        }
        if (ci != CommitState.Success) return null;

        // Approval at head: recorded reviewer verdict, or a formal
        // review at the current head.
        var recordedApprove =
            string.Equals(task.GetMetadata("reviewSha"), sha, StringComparison.Ordinal)
            && task.GetMetadata("reviewVerdict") == "Approve";
        var formalApprove = false;
        try
        {
            if (reviewsOverride is not null)
            {
                formalApprove = reviewsOverride(prNumber).Contains(PullRequestReviewState.Approved);
            }
            else
            {
                var reviews = await _gitHub.GetReviewsAsync(prNumber, cancellationToken);
                formalApprove = reviews.Any(r => r.State.Value == PullRequestReviewState.Approved
                    && string.Equals(r.CommitId, sha, StringComparison.Ordinal));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Task {Id}: mergeable-resume reviews fetch failed; using recorded verdict only", task.Id);
        }
        if (!recordedApprove && !formalApprove) return null;

        _logger.LogInformation(
            "PR #{PrNumber} (task {Id}): blockage cleared externally — mergeable + CI green + approved at {Sha}; resuming watch",
            prNumber, task.Id, sha[..Math.Min(7, sha.Length)]);
        var resumed = await _issues.TransitionAsync(task.Id, IssueStatus.InProgress,
            $"auto-resumed (round {attempts + 1}/{MaxAutoResumeAttempts}): PR mergeable again — blockage cleared externally",
            new Dictionary<string, object>
            {
                ["blockedKind"] = null!,
                ["lastError"] = null!,
                ["lastErrorAt"] = null!,
                ["prOpenedAt"] = DateTime.UtcNow.ToString("O"),
                ["autoResumeAttempts"] = (attempts + 1).ToString(),
            }, ct: cancellationToken);
        await ReportLifecycleAsync(resumed, Forge.Core.TaskEvent.WatchResumed, cancellationToken);
        return resumed;
    }

    /// <summary>True when another task in this store has a
    /// conflict-sync round claimed AND LIVE (InProgress carrying the
    /// conflict rework reason with an active run). The conflict
    /// mutex: one sync at a time so parallel rounds stop racing a
    /// moving main (observed live 2026-07-31: PRs #739 + #742).
    ///
    /// <para>An InProgress conflict-claim with NO active run is an
    /// orphan (restart killed the sync round) — it must NOT hold the
    /// mutex, or every conflict sync defers behind a dead claim and
    /// the whole merge pipeline deadlocks (observed live 2026-08-01:
    /// task-18/20/364 all orphaned mid-sync; 367/370 deferred
    /// forever). Orphans re-enter through the sweep's normal
    /// rework requeue — the same poll that was deferring on their
    /// behalf.</para>
    /// </summary>
    private async Task<bool> ConflictSyncInFlightAsync(string selfTaskId, CancellationToken ct)
    {
        var claimed = (await _issues.ListAsync(
                new Forge.Core.IssueFilter { Status = IssueStatus.InProgress }, ct))
            .Where(t => t.Id != selfTaskId
                && t.GetMetadata("reworkReason") == ConflictReworkReason)
            .ToList();
        if (claimed.Count == 0) return false;
        if (_runs is null) return true;   // no registry — conservative
        var activeTaskIds = (await _runs.ListActiveAsync(ct))
            .Where(r => r.TaskId is not null)
            .Select(r => r.TaskId!)
            .ToHashSet(StringComparer.Ordinal);
        return claimed.Any(t => activeTaskIds.Contains(t.Id));
    }

    /// <summary>Phase 2 shadow-authority helper: report an observed
    /// event to the lifecycle machine. Best-effort — never breaks the
    /// watch loop.</summary>
    private async Task ReportLifecycleAsync(
        IssueRecord task, Forge.Core.TaskEvent evt, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object>? extraMetadata = null)
    {
        if (_lifecycle is null) return;
        try
        {
            var fresh = await _issues.GetAsync(task.Id, cancellationToken) ?? task;
            await _lifecycle.ReportAsync(_issues, fresh, evt, watch: null, hasActiveDevRun: false, cancellationToken, extraMetadata);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lifecycle report {Event} failed for {TaskId}; continuing", evt, task.Id);
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
        IssueRecord task,
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
        var taskId = task.Id;
        // Conflict-sync mutex (2026-07-31): one sync round at a time
        // per project. Concurrent syncs race a moving main — every
        // merge re-dirties the other conflicting PRs, so parallel
        // rounds keep re-conflicting each other and burn breaker
        // strikes without ever landing (observed live: PRs #739 +
        // #742 sync-raced four merges on main). While another task's
        // conflict-sync round is claimed, wait — the strike fires
        // once the base stops moving. Single choke point: every
        // conflict branch (MergeReady, CI-red, 2b) routes here.
        if (reason == ConflictReworkReason
            && await ConflictSyncInFlightAsync(taskId, cancellationToken))
        {
            _logger.LogInformation(
                "Task {Id}: conflict-sync round deferred — another sync is in flight; serializing",
                taskId);
            return WatchPollOutcome.Pending;
        }
        // Re-read: the caller's record predates the lifecycle reports
        // fired on the way here (BaseRecovered et al.), and the
        // metadata merge below must not clobber the machine's fresh
        // state writes with stale seeded values.
        var current = await _issues.GetAsync(taskId, cancellationToken) ?? task;
        var attempts = 0;
        {
            var raw = current.GetMetadata("reworkAttempts");
            if (raw is not null) int.TryParse(raw, out attempts);
        }

        if (countAsStrike && attempts >= (maxStrikes ?? MaxReworkAttempts))
        {
            // The circuit breaker is a TASK outcome: the loop gave up
            // after N rounds and the operator must intervene. Only the
            // task goes terminal — there is no watch row to fail
            // alongside it (operator rule 2026-07-29: a failed watch
            // read as "the review failed"; the breaker is not that).
            _logger.LogWarning("PR watch (task {TaskId}): circuit breaker tripped after {N} rework attempts ({Reason})",
                taskId, attempts, reason);
            await ReportLifecycleAsync(task, Forge.Core.TaskEvent.BreakerTripped, cancellationToken);
            await _issues.TransitionAsync(taskId, terminalStatus, terminalError, ct: cancellationToken);
            // Breaker-trip snapshot (operator 2026-08-01, postmortem
            // tooling): the WHY-bundle captured atomically at trip
            // time — task record + recent run outcomes + the reason
            // chain — under memory key breaker/<taskId>/<utc ticks>.
            // Without it a postmortem re-assembles the moment from
            // four stores by timestamp.
            await WriteBreakerSnapshotAsync(task, reason, attempts, cancellationToken);
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
            "PR watch (task {TaskId}): rework round {N}/{Max} — {Reason}",
            taskId, attempts, MaxReworkAttempts, reason);
        await ReportLifecycleAsync(task, Forge.Core.TaskEvent.ReworkFired, cancellationToken,
            extraMetadata: new Dictionary<string, object> { ["reworkForSha"] = headSha });
        var metadata = ParseMetadataDict(current.MetadataJson);
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

    /// <summary>
    /// Persist the breaker-trip WHY-bundle: task state, the strike
    /// chain, and the last few run outcomes (role/status/duration/
    /// error/dispatchId) as a single JSON artifact under memory key
    /// breaker/&lt;taskId&gt;/&lt;utcTicks&gt;. Best-effort — never
    /// blocks the terminal transition.
    /// </summary>
    private async Task WriteBreakerSnapshotAsync(IssueRecord task, string reason, int attempts, CancellationToken ct)
    {
        try
        {
            var recentRuns = _runs is null
                ? (object?)null
                : (await _runs.ListRecentAsync(5, taskId: task.Id, ct: ct))
                    .Select(r => new
                    {
                        r.Id, r.Role, r.Status, r.StartedAt, r.DurationMs, r.Error, r.DispatchId,
                    }).ToList();
            var snapshot = new
            {
                taskId = task.Id,
                title = task.Title,
                trippedAt = DateTime.UtcNow,
                reason,
                reworkAttempts = attempts,
                prNumber = task.GetMetadata("prNumber"),
                state = task.GetMetadata("state"),
                reviewVerdict = task.GetMetadata("reviewVerdict"),
                reviewSha = task.GetMetadata("reviewSha"),
                metadata = ParseMetadataDict(task.MetadataJson),
                recentRuns,
            };
            if (_issues is not Forge.Core.IssueStore concrete) return;
            await using var memory = new Forge.Core.MemoryStore(concrete.Db);
            var key = $"breaker/{task.Id}/{DateTime.UtcNow.Ticks}";
            await memory.RememberAsync(key,
                System.Text.Json.JsonSerializer.Serialize(snapshot), ttlDays: 30, ct);
            _logger.LogInformation("PR watch (task {TaskId}): breaker snapshot recorded at memory key {Key}", task.Id, key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "breaker snapshot write failed for {TaskId}; continuing", task.Id);
        }
    }

    private static Dictionary<string, object> ParseMetadataDict(string? metadataJson)
    {        var metadata = new Dictionary<string, object>();
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

    public async Task<int> ProcessWatchedTaskAsync(
        IssueRecord task,
        CancellationToken cancellationToken = default,
        Func<int, IReadOnlyList<PullRequestReviewState>>? reviewsOverride = null,
        Func<PullRequest, string>? headShaOverride = null)
    {
        var prText = task.GetMetadata("prNumber");
        if (!int.TryParse(prText, out _))
        {
            _logger.LogError("Watched task {Id} missing prNumber", task.Id);
            return 1;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await PollWatchedTaskAsync(
                task, cancellationToken, reviewsOverride, headShaOverride);
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
