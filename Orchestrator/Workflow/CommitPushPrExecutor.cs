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
        Forge.Core.Workflow.WorkflowResolver? workflow = null,
        IReadOnlyList<string>? verifyCommands = null,
        Func<IssueRecord, CancellationToken, Task>? onPrOpened = null)
        : base(
            "commit-push-pr",
            ExecutorFaultGuard.Wrap<AgentCompleted, PrOpened>("commit-push-pr", logger, (input, ctx, ct) => HandleAsync(input, issues, worktrees, gitHub, events, memoryExtractor, extractionStore, logger, workflow, verifyCommands, ct, onPrOpened)),
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
        IReadOnlyList<string>? verifyCommands = null,
        CancellationToken ct = default,
        Func<IssueRecord, CancellationToken, Task>? onPrOpened = null)
    {
        if (input.Result == AgentResult.Skipped)
        {
            return new PrOpened(input, PrResult.Skipped, 0, null);
        }
        var issue = input.Worktree.Claim.Issue;
        var branch = input.Worktree.Claim.Branch ?? $"agent/{issue.Id}";
        var worktreePath = input.Worktree.WorktreePath!;

        // Operator-park guard: a park (Blocked) can land while the run
        // is still in flight. The finishing run must not push, open a
        // PR, or transition the task — the park is the operator's call.
        // (Terminal states keep their dedicated no-diff handling below.)
        var prePush = await issues.GetAsync(issue.Id, ct);
        if (prePush?.Status is IssueStatus.Blocked)
        {
            logger.LogInformation(
                "CommitPushPr({Id}): task is Blocked (parked mid-run) — skipping push/PR",
                issue.Id);
            return new PrOpened(input, PrResult.Skipped, 0, null);
        }

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
            // The source of truth for "the branch carries new work"
            // is the unique-commit count against ORIGIN's base — the
            // worktree's local base ref can be stale and diff as a
            // false-positive "self-commit" (porthorizon task-7,
            // 2026-07-29: HEAD exactly on fresh origin/main diffed as
            // 9 files vs stale local main; the no-op push then died
            // on GitHub's no-commits 422, swallowed mid-pipeline).
            var aheadCount = await worktrees.GetAheadCountAsync(worktreePath, input.Worktree.BaseBranch, ct);
            if (aheadCount > 0)
            {
                var ahead = await worktrees.GetDiffStatsAsync(worktreePath, input.Worktree.BaseBranch, ct);
                hasChanges = true;
                logger.LogInformation(
                    "CommitPushPr({Id}): nothing to commit but branch is {Count} commit(s) ahead of {Base} — agent self-committed ({Summary}); proceeding to push/PR",
                    issue.Id, aheadCount, input.Worktree.BaseBranch, ahead.Summary);
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
            //
            // Plan-gate-blocked runs can NEVER legitimately complete
            // on no-diff: the run produced nothing because the gate
            // refused every plan (misrouted task, territory mismatch) —
            // not because the work was already done. A "the task is
            // unchanged, the gates block me" message must requeue as a
            // failure, not hollow-complete (observed live 2026-08-23:
            // porthorizon task-752, a client-scope task routed to
            // coredev, burned its plan revisions and closed Completed
            // without a single edit).
            var explicitNoOp = (input.Text ?? "")
                .Contains("NO_CHANGES_NEEDED", StringComparison.OrdinalIgnoreCase)
                && !PlanGateBlocked(current?.GetMetadata("planGate"));
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
                        new Dictionary<string, object>
                        {
                            ["noProgressAttempts"] = attempts.ToString(),
                            // Same context-carrying bounce: the next
                            // round's prompt renders this as "##
                            // Rework required" so the agent does the
                            // work instead of idling again.
                            ["reworkReason"] = explicitNoOp
                                ? $"NO_CHANGES_NEEDED rejected by policy (attempt {attempts})"
                                : $"no diff produced (attempt {attempts})",
                            ["reworkContext"] = "Your previous attempt produced NO changes — the task requires a real code change. " +
                                "Do the work: make the edits, run the tests, commit. If you genuinely believe nothing is needed, " +
                                "explain why in detail in your final response.\n\nLast response tail:\n" + Truncate(input.Text ?? "", 1500),
                        }, ct);
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

        // Pre-push hygiene gate (deterministic, runs BEFORE the
        // build/test verification): junk artifacts and oversized NEW
        // files never reach a PR — this class of mistake must not
        // depend on the reviewer catching it (observed live
        // 2026-07-30: porthorizon task-17 pushed
        // UmbilicalConnectorSystem.cs.bak, a working backup swept in
        // by git add -A; the reviewer burned a round flagging it).
        var addedFiles = await worktrees.GetAddedFilesAsync(worktreePath, input.Worktree.BaseBranch, ct);
        var hygieneViolations = AgentTools.PushHygiene.Check(worktreePath, addedFiles);

        // Pre-push verification gate: run the project's build/test
        // commands in the worktree BEFORE pushing. A failure here
        // bounces the task back to the agent with the output — no PR
        // churn, no watch round; GitHub CI stays the safety net.
        // verifyCommands: null = auto-detect (dotnet), empty = disabled.
        var commands = verifyCommands ?? AgentTools.RunVerification.DefaultCommands(worktreePath);
        if (hygieneViolations.Count > 0 || commands.Count > 0)
        {
            AgentTools.RunVerification.Result verification;
            if (hygieneViolations.Count > 0)
            {
                // Fast-fail on hygiene: no point burning build/test
                // minutes on a push that will be refused anyway.
                logger.LogWarning("CommitPushPr({Id}): pre-push hygiene violations: {Violations}", issue.Id, string.Join("; ", hygieneViolations));
                verification = new AgentTools.RunVerification.Result(false,
                    hygieneViolations.Select(v => "pre-push hygiene check failed (junk/oversized files added to the branch):\n" + v).ToList());
            }
            else
            {
                logger.LogInformation("CommitPushPr({Id}): running {Count} verification command(s) before push", issue.Id, commands.Count);
                verification = await AgentTools.RunVerification.RunAsync(worktreePath, commands, logger, ct);
            }
            if (!verification.Ok)
            {
                var attempts = int.TryParse(
                    (await issues.GetAsync(issue.Id, ct))?.GetMetadata("noProgressAttempts"), out var vn) ? vn + 1 : 1;
                var detail = string.Join("\n\n", verification.Failures);
                if (attempts >= MaxNoProgressAttempts)
                {
                    await issues.TransitionAsync(issue.Id, IssueStatus.Failed,
                        $"pre-push verification failed in {attempts} attempts",
                        new Dictionary<string, object>
                        {
                            ["noProgressAttempts"] = attempts.ToString(),
                            ["lastError"] = $"pre-push verification failed:\n{detail}",
                            ["lastErrorAt"] = DateTime.UtcNow.ToString("O"),
                        }, ct);
                    events.Publish(new DashboardEvent(
                        DateTime.UtcNow, DashboardEventKind.TaskTransition,
                        issue.Id, $"Failed (verification failed in {attempts} attempts)",
                        new Dictionary<string, object?> { ["response"] = Truncate(detail, 400) }));
                    logger.LogError("Issue {Id}: pre-push verification failed after {Attempts} attempts — Failed for operator review", issue.Id, attempts);
                }
                else
                {
                    // Bounce WITH context (operator rule 2026-07-31:
                    // a failed build/test returns to the run context
                    // to be fixed — it is NOT a task failure). The
                    // reworkReason/reworkContext pair is what
                    // RunAgentExecutor renders as "## Rework required"
                    // on the next round; without it the agent was
                    // blind and repeated the same failure.
                    await issues.TransitionAsync(issue.Id, IssueStatus.Pending,
                        $"pre-push verification failed (attempt {attempts}): {verification.Failures[0][..Math.Min(200, verification.Failures[0].Length)]}",
                        new Dictionary<string, object>
                        {
                            ["noProgressAttempts"] = attempts.ToString(),
                            ["lastError"] = $"pre-push verification failed:\n{detail}",
                            ["lastErrorAt"] = DateTime.UtcNow.ToString("O"),
                            ["reworkReason"] = $"pre-push verification failed (attempt {attempts})",
                            ["reworkContext"] = $"Your previous attempt's code FAILED the pre-push build/test verification — nothing was pushed. " +
                                $"Fix the failure below and re-run the build/tests yourself before finishing:\n\n{Truncate(detail, 2000)}",
                        }, ct);
                    events.Publish(new DashboardEvent(
                        DateTime.UtcNow, DashboardEventKind.TaskTransition,
                        issue.Id, $"Requeued (verification failed, attempt {attempts})",
                        new Dictionary<string, object?> { ["response"] = Truncate(detail, 400) }));
                    logger.LogWarning("Issue {Id}: pre-push verification failed — requeued with output (attempt {Attempts})", issue.Id, attempts);
                }
                return new PrOpened(input, PrResult.NoDiff, 0, null);
            }
            logger.LogInformation("CommitPushPr({Id}): verification passed", issue.Id);
            if (verification.FlakyPasses is { Count: > 0 } flakyPasses)
            {
                // Audit trail: the gate passed via the flaky-test
                // quarantine. The note rides the PR body / dashboard
                // so the operator sees which tests are poisoning
                // full-suite runs.
                foreach (var note in flakyPasses)
                {
                    logger.LogWarning("CommitPushPr({Id}): flaky quarantine — {Note}", issue.Id, note);
                    events.Publish(new DashboardEvent(
                        DateTime.UtcNow, DashboardEventKind.TaskTransition,
                        issue.Id, $"Verification passed with flaky-test quarantine: {Truncate(note, 300)}"));
                }
            }
        }

        // Rework divergence guard (2026-08-01): on a rework round
        // (prNumber already recorded) the branch MUST still contain
        // the PR head. An agent that resets onto main mid-round
        // builds a divergent branch whose push is rejected
        // non-fast-forward — and before this guard the throw vanished
        // into MAF's silent halt, so rounds did the work, could never
        // land it, and the stall guard burned strikes against a head
        // that couldn't move (observed live: task-377, strikes 2+3).
        // Bounce with explicit guidance; shares the no-progress
        // budget so a repeat offender trips the breaker instead of
        // looping forever.
        var prNumberForGuard = issue.GetMetadata("prNumber");
        if (!string.IsNullOrEmpty(prNumberForGuard)
            && !await worktrees.IsAncestorAsync(worktreePath, $"origin/{branch}", "HEAD", ct))
        {
            var divAttempts = int.TryParse(
                (await issues.GetAsync(issue.Id, ct))?.GetMetadata("noProgressAttempts"), out var dn) ? dn + 1 : 1;
            if (divAttempts >= MaxNoProgressAttempts)
            {
                await issues.TransitionAsync(issue.Id, IssueStatus.Failed,
                    $"rework branch diverged from PR head in {divAttempts} attempts",
                    new Dictionary<string, object>
                    {
                        ["lastError"] = "rework branch diverged from PR head (non-fast-forward push)",
                        ["lastErrorAt"] = DateTime.UtcNow.ToString("O"),
                    }, ct);
                return new PrOpened(input, PrResult.NoDiff, 0, null);
            }
            await issues.TransitionAsync(issue.Id, IssueStatus.Pending,
                "rework branch diverged from the PR head; requeued with guidance",
                new Dictionary<string, object>
                {
                    ["noProgressAttempts"] = divAttempts.ToString(),
                    ["lastError"] = "rework branch diverged from PR head (non-fast-forward push)",
                    ["lastErrorAt"] = DateTime.UtcNow.ToString("O"),
                    ["reworkReason"] = $"rework branch diverged from PR head (attempt {divAttempts})",
                    ["reworkContext"] = "Your previous attempt was built on a branch that does NOT contain the PR's current head — the push was rejected as non-fast-forward and nothing landed. Do NOT reset or rebase the branch onto main. The worktree starts synced to the PR head: build your changes ON TOP of that branch, and if main has moved, merge origin/main INTO the branch (do not reset to it).",
                }, ct);
            events.Publish(new DashboardEvent(
                DateTime.UtcNow, DashboardEventKind.TaskTransition,
                issue.Id, $"Requeued (branch diverged from PR head, attempt {divAttempts})", null));
            logger.LogWarning("CommitPushPr({Id}): rework branch diverged from PR head — push would be non-fast-forward; requeued (attempt {Attempts})", issue.Id, divAttempts);
            return new PrOpened(input, PrResult.NoDiff, 0, null);
        }

        // P4 Stage A: advance through the dispatch checkpoints so
        // a StartupRecovery pass can resume from push_done if we
        // crash between push and PR-open, or from pr_opened if we
        // crash after the PR is recorded.
        logger.LogInformation("CommitPushPr({Id}): setting CommitDone", issue.Id);
        await issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.CommitDone, ct);
        logger.LogInformation("CommitPushPr({Id}): calling PushAsync", issue.Id);
        try
        {
            await worktrees.PushAsync(worktreePath, branch, ct);
        }
        catch (Exception ex)
        {
            // MAF's in-process execution swallows executor faults —
            // the run halts and the orchestrator's halt guard sees a
            // checkpoint, never the error. Log here or the push
            // failure text is invisible (observed live 2026-08-01:
            // task-377's non-fast-forward rejections).
            logger.LogError(ex, "CommitPushPr({Id}): push failed", issue.Id);
            throw;
        }
        logger.LogInformation("CommitPushPr({Id}): push done, setting PushDone", issue.Id);
        await issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PushDone, ct);

        // Push success = progress: reset the no-progress counter and
        // drop the COMPLETED round's rework context — every reason is
        // written fresh at requeue time by the watcher, so anything
        // left here is stale guidance for later rounds (operator rule
        // 2026-07-31: rounds must be meaningful, budgets honest).
        // CRITICAL: this must clear conflict-sync reasons too
        // ("PR conflicts with the base branch") — a completed sync
        // round that keeps its reason satisfies the watcher's
        // conflict mutex (InProgress + conflict reason + any active
        // run, including its own background REVIEW run) and two such
        // tasks defer behind each other forever (observed live
        // 2026-08-09: task-447/task-453 deadlocked the merge pipeline
        // for 30+ minutes until the watchdog reaped both claims).
        var successClear = new Dictionary<string, object>
        {
            ["noProgressAttempts"] = null!,
            ["reworkReason"] = null!,
            ["reworkContext"] = null!,
        };
        await issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null, metadata: successClear, ct: ct);
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
                    title: PrText.Title(issue),
                    body: PrText.Body(issue, headSha, input.Text),
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
            // Stale-window anchor for the state-driven watch sweep.
            // Rework rounds reuse the PR — keep the ORIGINAL open time.
            if (!m.ContainsKey("prOpenedAt")) m["prOpenedAt"] = DateTime.UtcNow.ToString("O");
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

        // Event-driven review trigger (pause/resume architecture):
        // the reviewer starts on the pushed head NOW — while CI runs
        // — instead of waiting up to a sweep interval. The callback
        // is expected to be non-blocking (it schedules the review and
        // returns); the 15-min sweep stays the backstop. Failures
        // here must never break the dispatch.
        if (onPrOpened is not null)
        {
            try
            {
                var freshForReview = await issues.GetAsync(issue.Id, ct) ?? issue;
                await onPrOpened(freshForReview, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CommitPushPr({Id}): PR-opened review trigger failed; the sweep is the backstop", issue.Id);
            }
        }

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

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    /// <summary>True when the task's planGate audit shows the run burned
    /// its revision budget without ever getting an approved plan
    /// (failed=true). Missing/malformed metadata or an approved plan →
    /// false (the no-diff completion path stays available).</summary>
    internal static bool PlanGateBlocked(string? planGateJson)
    {
        if (string.IsNullOrWhiteSpace(planGateJson)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(planGateJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("failed", out var f)
                && f.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

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