using Microsoft.Extensions.Logging;
using Octokit;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using System.Text.Json;

namespace Forge.Reviewer;

/// <summary>
/// Reviews an exact PR revision, durably prepares the decision, and publishes a formal
/// review through the separate reviewer identity. An uncertain publication is reconciled
/// before any retry; it never becomes a metadata-only approval or an issue-comment fallback.
/// The watcher owns task transitions and the final merge decision.
/// </summary>
public sealed class ReviewerDispatcher
{
    private readonly IIssueStore _issues;
    private readonly GitHubService _gitHub;
    private readonly IAgentRunner _agentRunner;
    private readonly Forge.AgentTools.GitWorktreeService? _worktrees;
    private readonly Forge.Core.TaskStateMachine? _lifecycle;
    private readonly IDashboardEventBus? _events;
    private readonly string? _projectId;
    private readonly Forge.Core.Messaging.IEventPublisher? _eventPublisher;
    /// <summary>Watch-lane QA stage (project $qa flag): the reviewer
    /// never runs ahead of QA — a head without a current QA verdict is
    /// skipped (QA runs first; its completion relaunches the review).</summary>
    private readonly bool _qaEnabled;

    /// <summary>Hard cap on one reviewer LLM call. Must be bounded
    /// (see the call site: a hung call must not freeze the loop
    /// forever) but generous enough for an AGENTIC review — the
    /// reviewer explores the worktree with read-only bash and pages
    /// the full diff via pr_diff, so a thorough review is several
    /// LLM round-trips with tool calls between them. 3 minutes
    /// (pre-tools, one-paste reviews) systematically killed every
    /// review at the timeout — observed live 2026-07-31: porthorizon
    /// tasks 17/20 burned their whole auto-resume budget on
    /// TaskCanceledException reviews.</summary>
    internal static readonly TimeSpan ReviewRunTimeout = TimeSpan.FromMinutes(12);
    private readonly ILogger<ReviewerDispatcher> _logger;

    public ReviewerDispatcher(
        IIssueStore issues,
        GitHubService gitHub,
        IAgentRunner agentRunner,
        ILogger<ReviewerDispatcher> logger,
        Forge.Core.TaskStateMachine? lifecycle = null,
        IDashboardEventBus? events = null,
        string? projectId = null,
        Forge.Core.Messaging.IEventPublisher? eventPublisher = null,
        bool qaEnabled = false,
        Forge.AgentTools.GitWorktreeService? worktrees = null)
    {
        _issues = issues;
        _gitHub = gitHub;
        _agentRunner = agentRunner;
        _logger = logger;
        _lifecycle = lifecycle;
        _events = events;
        _projectId = projectId;
        _eventPublisher = eventPublisher;
        _qaEnabled = qaEnabled;
        _worktrees = worktrees;
    }

    public sealed record ReviewOutcome(
        ReviewerVerdict Verdict,
        string Body,
        string HeadSha,
        string? Error = null);

    /// <summary>
    /// Review the PR behind a watch issue once. Returns null when no
    /// review was needed (already reviewed at the current head SHA).
    /// Never throws for LLM/GitHub failures — those come back as an
    /// Error outcome so the watcher's circuit breaker can count them.
    /// </summary>
    public async Task<ReviewOutcome?> ReviewOnceAsync(
        IssueRecord task, CancellationToken cancellationToken = default,
        Func<PullRequest, string>? headShaOverride = null)
    {
        var gate = ReviewWorkerAttempt.Gate(_projectId, task.Id, AgentType.Reviewer);
        await gate.WaitAsync(cancellationToken);
        try
        {
            task = await _issues.GetAsync(task.Id, cancellationToken) ?? task;
            return await ReviewCoreAsync(task, cancellationToken, headShaOverride);
        }
        finally
        {
            try { await CleanupReviewSnapshotAsync(task.Id); }
            finally { gate.Release(); }
        }
    }

    private async Task CleanupReviewSnapshotAsync(string taskId)
    {
        if (_worktrees is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var task = await _issues.GetAsync(taskId, timeout.Token);
            var path = task?.GetMetadata("reviewSnapshotPath");
            if (task is null || path is null) return;
            var owner = task.GetMetadata("reviewSnapshotWorkerId");
            if (_agentRunner is IReviewWorkerLifecycle lifecycle && owner is not null)
            {
                var worker = await lifecycle.InspectReviewWorkerAsync(AgentType.Reviewer, _projectId!, task.Id, owner, false, timeout.Token);
                // A detached controller must retain both a live worker's checkout and an
                // unconsumed terminal result's checkout until the next controller adopts it.
                if (worker is { Terminal: false }
                    || worker is not null && task.GetMetadata("reviewWorkerId") == owner
                        && task.GetMetadata("reviewWorkerConsumed") != owner) return;
            }
            var retirement = task.GetMetadata("reviewSnapshotRetirement") is { } saved
                ? JsonSerializer.Deserialize<Forge.AgentTools.ReviewSnapshotRetirement>(saved, ReviewFindings.Json)
                    ?? throw new InvalidOperationException("Saved review retirement is invalid.")
                : _worktrees.PrepareReviewSnapshotRetirement(path);
            if (retirement is not null && task.GetMetadata("reviewSnapshotRetirement") is null)
                await UpdateWatchMetadataAsync(task, m =>
                {
                    m["reviewSnapshotRetirement"] = JsonSerializer.Serialize(retirement, ReviewFindings.Json);
                    return m;
                }, timeout.Token);
            await _worktrees.RemoveReviewSnapshotAsync(path, timeout.Token, retirement);
            await UpdateWatchMetadataAsync(task, m =>
            {
                m["reviewSnapshotPath"] = null!;
                m["reviewSnapshotWorkerId"] = null!;
                m["reviewSnapshotRetirement"] = null!;
                return m;
            }, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Task {TaskId}: review snapshot cleanup deferred", taskId);
        }
    }

    private async Task<ReviewOutcome?> ReviewCoreAsync(IssueRecord task, CancellationToken cancellationToken,
        Func<PullRequest, string>? headShaOverride)
    {
        if (ReviewIntegrationState.IsBlocked(task)) return null;
        var prText = task.GetMetadata("prNumber");
        if (!int.TryParse(prText, out var prNumber))
        {
            _logger.LogError("Watched task {Id} missing prNumber", task.Id);
            return new ReviewOutcome(ReviewerVerdict.Error, "", "", "missing prNumber");
        }

        PullRequest pr;
        try
        {
            pr = await _gitHub.GetPullRequestAsync(prNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PR #{Pr}: could not fetch for review", prNumber);
            return new ReviewOutcome(ReviewerVerdict.Error, "", "", $"GetPullRequest: {ex.Message}");
        }
        // Test seam: Octokit's PullRequest.Head is init-only (even
        // the e2e harness keeps SHA in a side channel); tests supply
        // the head SHA directly.
        var headSha = headShaOverride is not null ? headShaOverride(pr) : pr.Head.Sha;
        if (_gitHub.RequireFormalReview && ReviewIntegrationState.Exhausted(task, headSha)
            && task.GetMetadata("reviewDraft") is null) return null;
        try { await _gitHub.EnsureReviewerIdentityAsync(prNumber, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await RecordIntegrationFailureAsync(task, headSha, ex.Message, false, cancellationToken);
            return new(ReviewerVerdict.Error, "", headSha, ex.Message);
        }
        ReviewDraft? pending;
        try { pending = JsonSerializer.Deserialize<ReviewDraft>(task.GetMetadata("reviewDraft") ?? "null", ReviewFindings.Json); }
        catch (JsonException ex)
        {
            var draftError = "Saved review draft is invalid; operator reconciliation is required: " + ex.Message;
            await RecordIntegrationFailureAsync(task, headSha, draftError, true, cancellationToken);
            return new(ReviewerVerdict.Error, "", headSha, draftError);
        }
        if (pending is not null && pending.HeadSha == headSha)
            return !ReviewIntegrationState.Exhausted(task, headSha)
                || bool.TryParse(task.GetMetadata("reviewPublishStarted"), out var uncertain) && uncertain
                ? await CompleteDraftAsync(task, prNumber, pending, cancellationToken, headShaOverride) : null;

        if (pending is not null)
        {
            try
            {
                // Reconcile a lost response for the old revision, but never publish it or resolve
                // conversations using evidence that predates the current head.
                var published = await _gitHub.FindPublishedReviewAsync(prNumber, pending, ReviewFindings.Marker(pending), cancellationToken);
                await UpdateWatchMetadataAsync(task, m =>
                {
                    if (published is not null)
                    {
                        var history = m.TryGetValue("reviewHistory", out var raw)
                            ? JsonSerializer.Deserialize<List<PublishedReview>>(raw.ToString()!, ReviewFindings.Json) ?? [] : [];
                        if (!history.Any(r => r.HeadSha == published.HeadSha && r.Round == published.Round && r.Url == published.Url))
                            history.Add(published);
                        m["reviewHistory"] = JsonSerializer.Serialize(history, ReviewFindings.Json);
                    }
                    m["reviewSupersededDraft"] = JsonSerializer.Serialize(pending, ReviewFindings.Json);
                    m["reviewSupersededPublication"] = published is not null ? "published" :
                        bool.TryParse(task.GetMetadata("reviewPublishStarted"), out var started) && started ? "unconfirmed" : "not submitted";
                    m["reviewDraft"] = null!;
                    m["reviewPublishStarted"] = null!;
                    m["reviewStartedAt"] = null!;
                    return m;
                }, cancellationToken);
                task = await _issues.GetAsync(task.Id, cancellationToken) ?? task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await RecordIntegrationFailureAsync(task, headSha, "Superseded review reconciliation failed: " + ex.Message, false, cancellationToken);
                return new(ReviewerVerdict.Error, "", headSha, ex.Message);
            }
        }

        IReadOnlyList<ReviewConversation> conversations = [];
        if (_gitHub.RequireFormalReview)
        {
            try { conversations = (await _gitHub.GetMergeRequirementsAsync(prNumber, headSha, cancellationToken)).Conversations; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await RecordIntegrationFailureAsync(task, headSha, ex.Message, false, cancellationToken);
                return new(ReviewerVerdict.Error, "", headSha, "Review conversations unavailable: " + ex.Message);
            }
        }

        // Per-SHA dedupe: the watch sweep calls this every pass; only
        // a head move (rework push) triggers a fresh round. An Error
        // verdict does NOT dedupe — the sweep retries the review (the
        // watcher's circuit breaker bounds the retries).
        var reviewedSha = task.GetMetadata("reviewSha");
        var recordedVerdict = task.GetMetadata("reviewVerdict");
        var humanCheckpoint = ReviewFindings.HumanConversationCheckpoint(conversations, headSha, _gitHub.ReviewerLogin);
        if (_gitHub.RequireFormalReview && reviewedSha == headSha && recordedVerdict == nameof(ReviewerVerdict.Error)
            && int.TryParse(task.GetMetadata("reviewErrorAttempts"), out var errorAttempts) && errorAttempts >= 3)
            return null; // Bounded paid attempts; a new head or explicit operator reconciliation is required.
        if (string.Equals(reviewedSha, headSha, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(recordedVerdict)
            && recordedVerdict != nameof(ReviewerVerdict.Error)
            && (!_gitHub.RequireFormalReview || task.GetMetadata("reviewPublishedHead") == headSha)
            && !(task.GetMetadata("reviewProtectionHead") == headSha
                && bool.TryParse(task.GetMetadata("reviewAutomatedProvenancePending"), out var provenancePending) && provenancePending)
            && !(recordedVerdict == nameof(ReviewerVerdict.Approve)
                && conversations.Any(t => GitHubService.CanResolveConversation(t, _gitHub.ReviewerLogin))
                && ReviewFindings.ConversationCheckpoint(conversations, _gitHub.ReviewerLogin) != task.GetMetadata("reviewThreadCheckpoint"))
            && !(recordedVerdict == nameof(ReviewerVerdict.Approve) && humanCheckpoint is not null
                && humanCheckpoint != task.GetMetadata("reviewHumanThreadCheckpoint")))
        {
            return null;
        }

        // Rework-in-flight skip: a rework round was queued FOR THIS
        // HEAD and the dev agent hasn't pushed yet. Reviewing now
        // wastes a full review on a head that's about to be replaced
        // — the verdict is sha-stamped, so it would be discarded the
        // moment the rework push lands. Wait for the new head. The
        // round record lives on the task via the machine.
        var reworkSha = task.GetMetadata("reworkForSha");
        if (string.Equals(reworkSha, headSha, StringComparison.Ordinal))
        {
            return null;
        }

        // QA-stage sequencing (project $qa flag): QA verifies the head
        // BEFORE review. No current QA verdict at this head → the sweep
        // is (re)running QA right now; the review waits. A standing
        // fail verdict is the watcher's rework trigger — reviewing a
        // QA-failed head wastes a round on a verdict the watch will
        // discard.
        if (_qaEnabled)
        {
            var qaSha = task.GetMetadata("qaSha");
            var qaVerdict = task.GetMetadata("qaVerdict");
            if (!string.Equals(qaSha, headSha, StringComparison.Ordinal)
                || string.IsNullOrEmpty(qaVerdict)
                || string.Equals(qaVerdict, QaDispatcher.VerdictFail, StringComparison.Ordinal))
            {
                return null;
            }
        }

        if (_gitHub.RequireFormalReview)
        {
            try { conversations = await _gitHub.GetReviewConversationInputsAsync(conversations, headSha, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await RecordIntegrationFailureAsync(task, headSha, ex.Message, false, cancellationToken);
                return new(ReviewerVerdict.Error, "", headSha, "Review conversation evidence unavailable: " + ex.Message);
            }
        }

        // Worktree access is MANDATORY (operator rule 2026-07-30): the
        // reviewer cannot do its job from a diff paste — it needs the
        // branch checkout for full context. No usable worktree →
        // Error, retried next sweep; never a degraded paste-only
        // review.
        var worktreePath = task.GetMetadata("worktreePath");
        // Fresh refs so `git show <sha>` / `git diff origin/<base>...<sha>`
        // resolve even when the checkout lags the head (external push).
        var prepared = await ReviewWorkerAttempt.BeginAsync(_agentRunner, _issues, task, AgentType.Reviewer,
            _projectId, headSha, cancellationToken);
        task = prepared.Task;
        if (prepared.Existing is not null)
        {
            worktreePath = prepared.Existing.WorktreePath;
            // Adopt snapshots created before cleanup tracking was introduced, never the
            // legacy engineering checkout that an older worker may still reference.
            if (_worktrees?.IsReviewSnapshotPath(worktreePath) == true && task.GetMetadata("reviewSnapshotPath") is null)
                await UpdateWatchMetadataAsync(task, m =>
                {
                    m["reviewSnapshotPath"] = worktreePath;
                    m["reviewSnapshotWorkerId"] = task.GetMetadata("reviewWorkerId")!;
                    return m;
                }, cancellationToken);
        }
        else if (_worktrees is not null)
        {
            try
            {
                await CleanupReviewSnapshotAsync(task.Id);
                var fresh = await _issues.GetAsync(task.Id, cancellationToken) ?? task;
                if (fresh.GetMetadata("reviewSnapshotPath") is not null)
                    throw new InvalidOperationException("Previous review snapshot has not been retired.");
                worktreePath = await _worktrees.CreateReviewSnapshotAsync(task.Id,
                    (task.GetMetadata("reviewWorkerId") ?? "in-process") + "-" + Guid.NewGuid().ToString("N"), headSha, cancellationToken);
                try
                {
                    await UpdateWatchMetadataAsync(task, m =>
                    {
                        m["reviewSnapshotPath"] = worktreePath;
                        m["reviewSnapshotWorkerId"] = task.GetMetadata("reviewWorkerId")!;
                        return m;
                    }, cancellationToken);
                }
                catch
                {
                    // No worker can have started before its snapshot pointer is durable.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    try { await _worktrees.RemoveReviewSnapshotAsync(worktreePath, cleanup.Token); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Unrecorded review snapshot cleanup failed at {Path}", worktreePath); }
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await RecordIntegrationFailureAsync(task, headSha, "Review checkout preparation failed: " + ex.Message, false, cancellationToken);
                return new(ReviewerVerdict.Error, "", headSha, ex.Message);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
                return new ReviewOutcome(ReviewerVerdict.Error, "", headSha, "worktree missing");
            await TryFetchAsync(worktreePath, cancellationToken);
        }

        // Re-review resume: a prior verdict at an EARLIER head means
        // this round scopes to the incremental diff (what the rework
        // push changed) plus the prior findings to verify — not the
        // whole PR. The warm session (resumed by the runner under the
        // task's session key) carries the previous review's context;
        // the incremental framing bounds anchoring/rubber-stamping.
        var previousReviewSha = !string.IsNullOrEmpty(reviewedSha)
            && !string.Equals(reviewedSha, headSha, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(recordedVerdict)
            && recordedVerdict != nameof(ReviewerVerdict.Error)
            ? reviewedSha
            : null;

        // "Reviewing…" lifecycle for the dashboard: stamp
        // reviewStartedAt + publish the event when a review actually
        // starts (both the event-driven PR-open trigger and the sweep
        // path flow through here); cleared when the verdict lands.
        try
        {
            await UpdateWatchMetadataAsync(task, m =>
            {
                m["reviewStartedAt"] = DateTime.UtcNow.ToString("O");
                return m;
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "reviewStartedAt stamp failed for PR #{Pr}; continuing", prNumber);
        }
        _events?.Publish(new DashboardEvent(
            DateTime.UtcNow, DashboardEventKind.ReviewStarted, task.Id,
            $"PR #{prNumber} review started (sha {headSha[..Math.Min(7, headSha.Length)]})",
            new Dictionary<string, object?>
            {
                ["prNumber"] = prNumber,
                ["sha"] = headSha,
                ["incrementalSince"] = previousReviewSha,
            }));

        string diff;
        var incremental = false;
        if (previousReviewSha is not null)
        {
            try
            {
                diff = await _gitHub.GetCompareDiffAsync(previousReviewSha, headSha, cancellationToken);
                incremental = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PR #{Pr}: incremental diff {Base}..{Head} failed; falling back to the full PR diff",
                    prNumber, previousReviewSha, headSha);
                diff = await FetchPrDiffAsync(prNumber, cancellationToken);
            }
        }
        else
        {
            diff = await FetchPrDiffAsync(prNumber, cancellationToken);
        }

        var round = 1;
        if (int.TryParse(task.GetMetadata("reviewRound"), out var prior)) round = prior + 1;

        // Full PR context, retrieved programmatically (operator rule
        // 2026-07-30): commits, conversation, formal reviews, CI —
        // not left to the agent to dig up. Re-reviews scope commits +
        // diff to the updates since the last reviewed head.
        var reviewContext = await BuildReviewContextAsync(pr, prNumber, headSha, previousReviewSha, cancellationToken);
        var prompt = incremental
            ? BuildReReviewPrompt(pr, diff, task, previousReviewSha!, recordedVerdict!, task.GetMetadata("reviewNotes"), worktreePath, reviewContext)
            : BuildReviewerPrompt(pr, diff, task, worktreePath, headSha, reviewContext);
        prompt += "\n\n" + ReviewFindings.Instructions;
        foreach (var conversation in conversations.Where(t => !t.Resolved && t.AssessmentComplete))
            prompt += "\n\n" + ReviewFindings.ConversationPrompt(conversation, headSha, _gitHub.ReviewerLogin);
        if (conversations.Any(t => !t.Resolved && !t.AssessmentComplete))
            prompt += "\n\nSome discussions exceed the bounded review input. They stay unresolved for their participants. Do not claim those findings are verified.";
        ReviewerVerdict verdict;
        string body;
        string? error = null;
        try
        {
            // Context carries the watched task id so the Reviewer's
            // file_followup tool can defer non-blocking findings as
            // groomable follow-up tasks (parented via metadata).
            // projectId scopes the resumed reviewer session to this
            // project (session key: session/<project>/<task>/<role>).
            var runnerContext = new Dictionary<string, object>
            {
                ["issueId"] = task.Id,
            };
            if (!string.IsNullOrWhiteSpace(_projectId))
            {
                runnerContext["projectId"] = _projectId;
            }
            // Worktree access: the diff paste is bounded — the
            // reviewer inspects the PR branch itself (read-only bash,
            // hard-enforced in MafAgentRunner) and pages the full diff
            // via the pr_diff tool (fed through reviewDiff).
            runnerContext["worktreePath"] = worktreePath;
            if (diff.Length > 0)
            {
                runnerContext["reviewDiff"] = diff;
            }
            ReviewWorkerAttempt.Bind(prepared.Existing?.Context, task, AgentType.Reviewer, headSha, runnerContext);
            // Bounded call: the reviewer runs inside the watch sweep,
            // which shares the orchestrator's main loop — an
            // unbounded LLM hang (SDK retries × 5-min network
            // timeout) freezes dispatch AND all watches (observed
            // live 2026-07-26: 20+ min frozen loop on a hung kimi
            // call). A timeout is an Error verdict (no silent
            // approval), retried next sweep, bounded by the existing
            // review circuit breaker.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ReviewRunTimeout);
            var result = await _agentRunner.RunAsync(
                AgentType.Reviewer, prompt, sessionId: null, context: runnerContext, ct: timeoutCts.Token);
            (verdict, body) = ParseReviewerOutput(result.Text);
            if (verdict == ReviewerVerdict.Error) error = "Reviewer output did not end with an explicit valid verdict.";
        }
        catch (OperationCanceledException) when (_agentRunner is IReviewWorkerLifecycle)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null; // Controller wait detached; keep the same durable attempt for adoption.
        }
        catch (Exception ex)
        {
            // No silent approvals: an unavailable reviewer is an
            // Error outcome (the circuit breaker escalates to the
            // operator), never an Approve.
            _logger.LogWarning(ex, "Reviewer LLM call failed for PR #{Pr}", prNumber);
            verdict = ReviewerVerdict.Error;
            body = "";
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        var consumed = await ReviewWorkerAttempt.ConsumedAsync(_agentRunner, task, AgentType.Reviewer, _projectId, cancellationToken);
        if (_agentRunner is IReviewWorkerLifecycle && consumed is null)
            return null; // Uncertain/in-flight worker is not a completed review attempt.

        if (verdict != ReviewerVerdict.Error)
        {
            // Anchors always use the full PR diff, even when the model reviewed an incremental diff.
            string fullDiff;
            try { fullDiff = incremental ? await _gitHub.GetPullRequestDiffAsync(prNumber, cancellationToken) : diff; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Full PR diff unavailable for #{Pr}; retaining findings in the review summary without anchors", prNumber);
                fullDiff = "";
            }
            ReviewDraft draft;
            try { draft = ReviewFindings.Create(round, headSha, verdict.ToString(), body, fullDiff, conversations, _gitHub.ReviewerLogin); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                await RecordIntegrationFailureAsync(task, headSha, ex.Message, false, cancellationToken);
                return new(ReviewerVerdict.Error, body, headSha, ex.Message);
            }
            await UpdateWatchMetadataAsync(task, m =>
            {
                m["reviewDraft"] = JsonSerializer.Serialize(draft, ReviewFindings.Json);
                m["reviewPublishStarted"] = false;
                m["reviewWorkerConsumed"] = consumed!;
                return m;
            }, cancellationToken);
            return await CompleteDraftAsync(task, prNumber, draft, cancellationToken, headShaOverride);
        }
        // Record the verdict in the task metadata — the PRWatcher's
        // merge/rework decision reads this. The verdict landing
        // clears the "reviewing…" marker (metadata is upsert-merge
        // only — JSON null is the delete idiom).
        await UpdateWatchMetadataAsync(task, m =>
        {
            m["reviewWorkerConsumed"] = consumed!;
            m["reviewSha"] = headSha;
            m["reviewVerdict"] = verdict.ToString();
            var priorErrors = task.GetMetadata("reviewSha") == headSha
                && int.TryParse(task.GetMetadata("reviewErrorAttempts"), out var parsedErrors) ? parsedErrors : 0;
            m["reviewErrorAttempts"] = priorErrors + 1;
            if (_gitHub.RequireFormalReview && priorErrors + 1 >= 3)
                m["reviewIntegrationError"] = "Automated review produced no usable verdict after three attempts at this revision. Further paid attempts are paused for operator reconciliation.";
            m["reviewNotes"] = body.Length > 2000 ? body[..2000] : body;
            m["reviewRound"] = round;
            m["reviewStartedAt"] = null!;
            if (error is not null) m["reviewError"] = error;
            else m.Remove("reviewError");
            return m;
        }, cancellationToken);

        _logger.LogInformation("PR #{Pr}: reviewer verdict {Verdict} (round {Round}, sha {Sha})",
            prNumber, verdict, round, headSha[..Math.Min(7, headSha.Length)]);

        // Message-driven watch pipeline: the verdict landing kicks an
        // immediate merge/rework decision (consumer polls this one PR)
        // instead of waiting out the sweep interval. Hint only — the
        // publisher swallows failures; the 15m sweep is the backstop.
        if (_eventPublisher is not null)
        {
            await _eventPublisher.PublishAsync(new Forge.Core.Messaging.ReviewVerdictRecorded
            {
                MessageId = Forge.Core.Messaging.ReviewVerdictRecorded.IdFor(_projectId ?? "default", task.Id, prNumber, headSha, round),
                ProjectId = _projectId ?? "default",
                TaskId = task.Id,
                PrNumber = prNumber,
                Verdict = verdict.ToString(),
                ReviewSha = headSha,
                ReviewRound = round,
            }, cancellationToken);
        }

        // Phase 2 shadow authority: report the verdict to the
        // lifecycle machine (best-effort; never breaks a review).
        if (_lifecycle is not null
            && verdict is ReviewerVerdict.Approve or ReviewerVerdict.RequestChanges)
        {
            try
            {
                var fresh = await _issues.GetAsync(task.Id, cancellationToken) ?? task;
                await _lifecycle.ReportAsync(_issues, fresh,
                    verdict == ReviewerVerdict.Approve
                        ? Forge.Core.TaskEvent.ReviewApproved
                        : Forge.Core.TaskEvent.ReviewChangesRequested,
                    watch: null, hasActiveDevRun: false, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "lifecycle report for PR #{Pr} verdict failed; continuing", prNumber);
            }
        }
        return new ReviewOutcome(verdict, body, headSha, error);
    }

    private async Task<ReviewOutcome> CompleteDraftAsync(IssueRecord task, int prNumber, ReviewDraft draft,
        CancellationToken ct, Func<PullRequest, string>? headOverride)
    {
        if (!Enum.TryParse<ReviewerVerdict>(draft.Verdict, out var verdict)
            || verdict is not (ReviewerVerdict.Approve or ReviewerVerdict.RequestChanges))
        {
            await RecordIntegrationFailureAsync(task, draft.HeadSha,
                "Saved draft has no publishable verdict; operator reconciliation is required.", true, ct);
            return new(ReviewerVerdict.Error, "", draft.HeadSha, "Saved draft has no publishable verdict.");
        }
        var marker = ReviewFindings.Marker(draft);
        try
        {
            var published = await _gitHub.FindPublishedReviewAsync(prNumber, draft, marker, ct);
            if (published is null)
            {
                var fresh = await _issues.GetAsync(task.Id, ct) ?? throw new InvalidOperationException("Task no longer exists.");
                if (bool.TryParse(fresh.GetMetadata("reviewPublishStarted"), out var uncertain) && uncertain)
                    throw new InvalidOperationException("Review publication is unconfirmed. No matching formal review was found; automatic duplicate submission is held for reconciliation.");
                var currentPr = await _gitHub.GetPullRequestAsync(prNumber, ct);
                var currentHead = headOverride is null ? currentPr.Head.Sha : headOverride(currentPr);
                if (currentHead != draft.HeadSha || currentPr.Merged || currentPr.State == ItemState.Closed)
                    throw new InvalidOperationException("PR changed or closed before review publication; the saved review was not submitted.");
                await UpdateWatchMetadataAsync(task, m => { m["reviewPublishStarted"] = true; m["reviewIntegrationError"] = null!; return m; }, ct);
                try { published = await _gitHub.PublishFormalReviewAsync(prNumber, draft, marker, ct); }
                catch (ApiException ex) when (IsDefinitePublicationRefusal(ex))
                {
                    // GitHub rejected the request without creating a review. Keep the draft, not a new paid run.
                    await UpdateWatchMetadataAsync(task, m => { m["reviewPublishStarted"] = false; return m; }, ct);
                    throw;
                }
            }
            var conversationReceipts = await _gitHub.ResolveReviewedConversationsAsync(prNumber, draft, ct);
            var humanVersions = draft.HumanThreadVersions is null ? null : new Dictionary<string, string>(draft.HumanThreadVersions);
            if (humanVersions is not null)
                foreach (var receipt in conversationReceipts) humanVersions[receipt.Key] = receipt.Value;
            await UpdateWatchMetadataAsync(task, m =>
            {
                var history = new List<PublishedReview>();
                m["reviewIntegrationAttempts"] = 0;
                if (m.TryGetValue("reviewHistory", out var raw))
                    history = JsonSerializer.Deserialize<List<PublishedReview>>(raw.ToString()!, ReviewFindings.Json) ?? [];
                if (history.Count == 0 && m.TryGetValue("reviewSha", out var oldHead) && m.TryGetValue("reviewVerdict", out var oldVerdict)
                    && oldVerdict.ToString() is "Approve" or "RequestChanges")
                {
                    var oldRound = m.TryGetValue("reviewRound", out var number) && int.TryParse(number.ToString(), out var parsedRound) ? parsedRound : 0;
                    history.Add(new(oldRound, oldHead.ToString()!, oldVerdict.ToString()!,
                        m.GetValueOrDefault("reviewNotes")?.ToString() ?? "No notes retained.",
                        "Legacy Forge verdict · formal publication not recorded", "", null, []));
                }
                if (!history.Any(r => r.HeadSha == published.HeadSha && r.Round == published.Round && r.Url == published.Url)) history.Add(published);
                m["reviewHistory"] = JsonSerializer.Serialize(history, ReviewFindings.Json);
                m["reviewSha"] = draft.HeadSha; m["reviewVerdict"] = draft.Verdict;
                m["reviewNotes"] = draft.Summary; m["reviewRound"] = draft.Round;
                m["reviewPublishedHead"] = draft.HeadSha; m["reviewDraft"] = null!;
                m["reviewAutomatedProvenancePending"] = false;
                m["reviewThreadCheckpoint"] = draft.ThreadCheckpoint!;
                m["reviewHumanThreadCheckpoint"] = (humanVersions is null ? draft.HumanThreadCheckpoint : ReviewFindings.HumanCheckpoint(humanVersions))!;
                m["reviewConversationInputLimited"] = draft.LimitedConversations?.Count ?? 0;
                m["reviewConversationInputHead"] = draft.HeadSha;
                m["reviewConversationLimitedIds"] = draft.LimitedConversations is null ? null! : JsonSerializer.Serialize(draft.LimitedConversations);
                m["reviewPublishStarted"] = null!; m["reviewStartedAt"] = null!;
                m["reviewError"] = null!; m["reviewIntegrationError"] = null!;
                m["reviewErrorAttempts"] = null!;
                return m;
            }, ct);
            if (_eventPublisher is not null) await _eventPublisher.PublishAsync(new Forge.Core.Messaging.ReviewVerdictRecorded
            {
                MessageId = Forge.Core.Messaging.ReviewVerdictRecorded.IdFor(_projectId ?? "default", task.Id, prNumber, draft.HeadSha, draft.Round),
                ProjectId = _projectId ?? "default", TaskId = task.Id, PrNumber = prNumber,
                Verdict = draft.Verdict, ReviewSha = draft.HeadSha, ReviewRound = draft.Round
            }, ct);
            var latestPr = await _gitHub.GetPullRequestAsync(prNumber, ct);
            var latestHead = headOverride is null ? latestPr.Head.Sha : headOverride(latestPr);
            if (latestHead != draft.HeadSha) return new(ReviewerVerdict.Error, draft.Summary, draft.HeadSha, "Head changed after publication; review retained as historical evidence.");
            if (_lifecycle is not null)
            {
                var fresh = await _issues.GetAsync(task.Id, ct);
                if (fresh is not null) await _lifecycle.ReportAsync(_issues, fresh,
                    verdict == ReviewerVerdict.Approve ? Forge.Core.TaskEvent.ReviewApproved : Forge.Core.TaskEvent.ReviewChangesRequested,
                    null, false, ct);
            }
            return new(verdict, draft.Summary, draft.HeadSha);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PR #{Pr}: formal review publication/reconciliation failed", prNumber);
            if (!ct.IsCancellationRequested)
            {
                var fresh = await _issues.GetAsync(task.Id, ct) ?? task;
                await RecordIntegrationFailureAsync(fresh, draft.HeadSha, ex.Message,
                    bool.TryParse(fresh.GetMetadata("reviewPublishStarted"), out var started) && started, ct);
            }
            return new(ReviewerVerdict.Error, draft.Summary, draft.HeadSha, "Review publication: " + ex.Message);
        }
    }

    internal static bool IsDefinitePublicationRefusal(ApiException error) => error is ApiValidationException
        || error.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity
            or System.Net.HttpStatusCode.TooManyRequests;

    private Task RecordIntegrationFailureAsync(IssueRecord task, string head, string error, bool uncertain, CancellationToken ct) =>
        UpdateWatchMetadataAsync(task, metadata =>
        {
            foreach (var field in ReviewIntegrationState.Failure(task, head, error, uncertain)) metadata[field.Key] = field.Value;
            return metadata;
        }, ct);

    /// <summary>
    /// Back-compat wrapper for the HTTP endpoint: review once and
    /// return a process-style exit code.
    /// </summary>
    public async Task<int> ProcessWatchedTaskAsync(
        IssueRecord task,
        CancellationToken cancellationToken = default)
    {
        var outcome = await ReviewOnceAsync(task, cancellationToken);
        return outcome is null || outcome.Error is null ? 0 : 1;
    }

    private async Task UpdateWatchMetadataAsync(
        IssueRecord task,
        Func<Dictionary<string, object>, Dictionary<string, object>> mutate,
        CancellationToken ct)
    {
        var cur = await _issues.GetAsync(task.Id, ct);
        if (cur is null) throw new InvalidOperationException("Task disappeared before review evidence could be saved.");
        var current = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(cur.MetadataJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(cur.MetadataJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                        current[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? p.Value.GetString()!
                            : p.Value.GetRawText();
                    }
                }
            }
            catch (JsonException ex) { throw new InvalidDataException("Task metadata is malformed; review publication was not advanced.", ex); }
        }
        var before = new Dictionary<string, object>(current);
        var next = mutate(current);
        var patch = next.Where(pair => !before.TryGetValue(pair.Key, out var old) || !Equals(old, pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var key in before.Keys.Except(next.Keys)) patch[key] = null!;
        if (_issues is IssueStore concrete)
            await concrete.PatchReviewMetadataAsync(cur.Id, patch, null, ct);
        else await _issues.TransitionAsync(cur.Id, cur.Status, error: null, metadata: patch, ct: ct);
    }

    /// <summary>Full PR context assembled programmatically for the
    /// review prompt. Every fetch is independently best-effort — a
    /// failed section renders as "(unavailable)", never kills the
    /// review.</summary>
    private sealed record ReviewContext(
        IReadOnlyList<GitHubService.PrCommit> Commits,
        IReadOnlyList<GitHubService.PrComment> Comments,
        IReadOnlyList<PullRequestReview> FormalReviews,
        CommitState? Ci,
        IReadOnlyList<string> FailingChecks);

    private async Task<ReviewContext> BuildReviewContextAsync(
        PullRequest pr, int prNumber, string headSha, string? previousReviewSha, CancellationToken cancellationToken)
    {
        IReadOnlyList<GitHubService.PrCommit> commits = Array.Empty<GitHubService.PrCommit>();
        try
        {
            commits = previousReviewSha is not null
                ? await _gitHub.GetCompareCommitsAsync(previousReviewSha, headSha, cancellationToken)
                : await GetPrCommitsFallbackAsync(prNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PR #{Pr}: commit list fetch failed; continuing", prNumber);
        }

        IReadOnlyList<GitHubService.PrComment> comments = Array.Empty<GitHubService.PrComment>();
        try
        {
            comments = await _gitHub.GetIssueCommentsAsync(prNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PR #{Pr}: conversation fetch failed; continuing", prNumber);
        }

        IReadOnlyList<PullRequestReview> formalReviews = Array.Empty<PullRequestReview>();
        try
        {
            formalReviews = await _gitHub.GetReviewsAsync(prNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PR #{Pr}: formal reviews fetch failed; continuing", prNumber);
        }

        CommitState? ci = null;
        IReadOnlyList<string> failing = Array.Empty<string>();
        try
        {
            ci = await _gitHub.GetCommitStatusAsync(headSha, cancellationToken);
            if (ci is CommitState.Failure or CommitState.Error)
            {
                failing = await _gitHub.GetFailedCheckRunSummariesAsync(headSha, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PR #{Pr}: CI status fetch failed; continuing", prNumber);
        }

        return new ReviewContext(commits, comments, formalReviews, ci, failing);
    }

    private async Task<IReadOnlyList<GitHubService.PrCommit>> GetPrCommitsFallbackAsync(
        int prNumber, CancellationToken cancellationToken)
    {
        // First review: the full commit list via the PR's own
        // base..head compare.
        var pr = await _gitHub.GetPullRequestAsync(prNumber, cancellationToken);
        if (pr.Base?.Sha is null || pr.Head?.Sha is null) return Array.Empty<GitHubService.PrCommit>();
        return await _gitHub.GetCompareCommitsAsync(pr.Base.Sha, pr.Head.Sha, cancellationToken);
    }

    /// <summary>Best-effort <c>git fetch origin</c> in the review
    /// worktree — keeps refs current so git-inspection instructions
    /// resolve against the head under review. Never throws.</summary>
    private async Task TryFetchAsync(string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FORGE_GITHUB_APPS_FILE")))
            {
                var scope = GitHubAppIdentities.FromUrl($"https://github.com/{_gitHub.Owner}/{_gitHub.Repo}");
                var result = await Forge.AgentTools.ControllerGitRunner.RunAsync(worktreePath, ["fetch", "origin"], scope, cancellationToken);
                if (result.ExitCode != 0) _logger.LogDebug("Isolated review fetch failed for {Path}", worktreePath);
                return;
            }
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "-C \"" + worktreePath + "\" fetch origin",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.Start();
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "review worktree fetch failed for {Path}; continuing with stale refs", worktreePath);
        }
    }

    /// <summary>Upper bound for the inlined diff. The reviewer has
    /// both the worktree and the paginated <c>pr_diff</c> tool (fed
    /// the full diff), so the inline paste is orientation: whole
    /// files until the budget fills, with an explicit manifest of the
    /// rest — never a silent mid-file cut.</summary>
    private const int MaxInlineDiffChars = 30_000;

    /// <summary>Format the unified diff for the prompt: full inline
    /// under the budget; above it, whole files in order until the
    /// budget fills, with an explicit manifest of omitted files
    /// (readable in the worktree). Internal for tests.</summary>
    internal static string FormatDiffForPrompt(string diff)
    {
        if (diff.Length <= MaxInlineDiffChars) return diff;

        var files = diff.Split("\ndiff --git ");
        var sb = new System.Text.StringBuilder(diff.Length);
        var omitted = new List<string>();
        for (var i = 0; i < files.Length; i++)
        {
            var chunk = i == 0 ? files[i] : "diff --git " + files[i];
            if (i > 0 && sb.Length + chunk.Length > MaxInlineDiffChars)
            {
                var firstLine = files[i].Split('\n', 2)[0];
                omitted.Add(firstLine.Trim());
                continue;
            }
            if (i == 0 && chunk.Length > MaxInlineDiffChars)
            {
                // A single file bigger than the budget: keep the head
                // of it and defer the rest to the worktree.
                sb.Append(chunk[..MaxInlineDiffChars]);
                omitted.Add(files[0].Split('\n', 2)[0].Trim() + " (remainder)");
                break;
            }
            sb.Append(chunk);
        }
        if (omitted.Count > 0)
        {
            sb.Append("\n...[").Append(omitted.Count)
                .Append(" file(s) omitted for size — page through them with the pr_diff tool or read them in the worktree:\n");
            foreach (var f in omitted) sb.Append("- ").Append(f).Append('\n');
            sb.Append(']');
        }
        return sb.ToString();
    }

    private static string RenderContext(ReviewContext ctx, bool incremental)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(incremental
            ? "Commits since your last review (new work to verify):\n"
            : "Commits on this PR:\n");
        if (ctx.Commits.Count == 0) sb.Append("  (unavailable)\n");
        foreach (var c in ctx.Commits)
        {
            var firstLine = c.Message.Split('\n', 2)[0];
            sb.Append("  ").Append(c.Sha[..Math.Min(7, c.Sha.Length)]).Append(' ').Append(firstLine).Append('\n');
        }

        sb.Append("\nCI at the head under review: ");
        sb.Append(ctx.Ci?.ToString() ?? "(unavailable)");
        if (ctx.FailingChecks.Count > 0)
        {
            sb.Append('\n');
            foreach (var f in ctx.FailingChecks) sb.Append("  FAIL: ").Append(f).Append('\n');
        }
        sb.Append('\n');

        sb.Append("\nPR conversation (oldest first — operator comments are review input):\n");
        if (ctx.Comments.Count == 0) sb.Append("  (no comments)\n");
        foreach (var c in ctx.Comments.TakeLast(50))
        {
            var body = c.Body.Length > 2000 ? c.Body[..2000] + "…" : c.Body;
            sb.Append("  [").Append(c.CreatedAt.ToString("yyyy-MM-dd HH:mm")).Append("] ")
                .Append(c.Author).Append(": ").Append(body).Append('\n');
        }

        sb.Append("\nFormal reviews:\n");
        if (ctx.FormalReviews.Count == 0) sb.Append("  (none)\n");
        foreach (var r in ctx.FormalReviews)
        {
            var body = string.IsNullOrWhiteSpace(r.Body) ? "" : $": {(r.Body.Length > 1000 ? r.Body[..1000] + "…" : r.Body)}";
            sb.Append("  ").Append(r.User?.Login ?? "unknown").Append(" — ").Append(r.State.ToString()).Append(body).Append('\n');
        }
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Worktree instructions shared by both review prompts.
    /// The checkout is the evidence; the pasted diff is (possibly
    /// size-bounded) orientation. Verdict discipline lives here too —
    /// a bounded paste or "unconfirmed coverage" is never a blocking
    /// reason on its own (observed live 2026-07-30: porthorizon
    /// task-17 burned its whole rework budget on a review that
    /// blocked partly on what a 12k paste didn't show).</summary>
    private static string WorktreeInstructions(string worktreePath, string? headSha, string baseBranch)
    {
        var shaNote = string.IsNullOrWhiteSpace(headSha) ? "" : $" The head under review is {headSha}.\n";
        return $"The PR branch is checked out at your bash working directory: {worktreePath}\n" +
               shaNote +
               "Before judging, ground yourself in the actual branch:\n" +
               "1. `git log -1 --format=%H` — confirm the checkout matches the head under review; " +
               "if it lags, trust git over the checkout (`git show <sha>`, `git diff origin/" + baseBranch + "...<sha>`).\n" +
               $"2. `git diff origin/{baseBranch}...HEAD --stat` — the full file list; then read any file " +
               "in full (`cat`, `sed -n`), inspect history (`git log`, `git show`), and run `dotnet build`/" +
               "`dotnet test` when a claim needs verification.\n" +
               "You are READ-ONLY: mutating commands are refused by the tooling.\n" +
               "Verdict discipline:\n" +
               "- REQUEST_CHANGES only for concrete findings in code you actually read — cite file:line.\n" +
               "- NEVER request changes because a paste was bounded or coverage is \"unconfirmed\" — " +
               "inspect the checkout and confirm or falsify first.\n" +
               "- Non-blocking findings (nits, adjacent debt, future improvements) do NOT block: approve " +
               "and file them with the file_followup tool instead.\n\n";
    }

    private static string BuildReviewerPrompt(
        PullRequest pr, string diff, IssueRecord task, string worktreePath, string headSha, ReviewContext ctx)
    {
        var taskTitle = task.GetMetadata("taskTitle") ?? task.Title;
        return $"You are the Reviewer role for Forge, evaluating a pull request against its task.\n\n" +
               $"Task: {taskTitle}\n" +
               $"PR: {pr.Title} (#{pr.Number})\n" +
               $"Body:\n{pr.Body}\n\n" +
               RenderContext(ctx, incremental: false) +
               WorktreeInstructions(worktreePath, headSha, pr.Base?.Ref ?? "main") +
               "The full diff is ALSO available through your `pr_diff` tool: call it with no arguments " +
               "for the file manifest, then page through any file or window you need in full. The paste " +
               "below is bounded for size — drill in before judging anything you cannot see.\n\n" +
               $"Unified diff (size-bounded, whole files only):\n```diff\n{FormatDiffForPrompt(diff)}\n```\n\n" +
               "Check that the changes implement the task, are self-contained, follow the repo's " +
               "conventions, and don't introduce dead code, unrelated rewrites, or artifacts that " +
               "don't belong in version control. Respond with your assessment, then the verdict " +
               "marker on its own line at the END of your reply:\n\n" +
               "REVIEWER_VERDICT: APPROVE | REQUEST_CHANGES\n\n" +
               "If REQUEST_CHANGES, precede it with a REVIEWER_NOTES: section listing the concrete " +
               "issues the engineer must fix (file + what to change). Be specific — these notes go " +
               "straight back to the engineer agent as rework instructions.";
    }

    private async Task<string> FetchPrDiffAsync(int prNumber, CancellationToken cancellationToken)
    {
        try
        {
            return await _gitHub.GetPullRequestDiffAsync(prNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch diff for PR #{Pr}; reviewing with empty diff", prNumber);
            return "";
        }
    }

    /// <summary>
    /// Re-review prompt for a head that moved since the last verdict:
    /// the reviewer resumes its warm session, verifies each prior
    /// finding against the INCREMENTAL diff, and flags only NEW
    /// blocking issues introduced by the new commits. The framing
    /// bounds warm-session anchoring: the model is told exactly what
    /// to re-check and what not to re-litigate.
    /// </summary>
    private static string BuildReReviewPrompt(
        PullRequest pr, string incrementalDiff, IssueRecord task,
        string previousReviewSha, string previousVerdict, string? previousNotes,
        string worktreePath, ReviewContext ctx)
    {
        var taskTitle = task.GetMetadata("taskTitle") ?? task.Title;
        return $"You are the Reviewer role for Forge, RE-REVIEWING a pull request after a rework push.\n\n" +
               $"Task: {taskTitle}\n" +
               $"PR: {pr.Title} (#{pr.Number})\n\n" +
               RenderContext(ctx, incremental: true) +
               WorktreeInstructions(worktreePath, headSha: null, pr.Base?.Ref ?? "main") +
               $"You previously reviewed this PR at commit {previousReviewSha[..Math.Min(7, previousReviewSha.Length)]} " +
               $"and returned {previousVerdict} with these notes:\n" +
               $"{(string.IsNullOrWhiteSpace(previousNotes) ? "(no notes recorded)" : previousNotes)}\n\n" +
               $"The engineer has pushed new commits. The diff below covers ONLY the changes since your " +
               $"last reviewed head ({previousReviewSha[..Math.Min(7, previousReviewSha.Length)]}..HEAD), " +
               "size-bounded to whole files — the `pr_diff` tool pages the full incremental diff " +
               "(manifest with no arguments):\n" +
               $"```diff\n{FormatDiffForPrompt(incrementalDiff)}\n```\n\n" +
               "Your job for this round:\n" +
               "1. Verify each of your prior findings is addressed by the new commits — against the " +
               "checkout and the full diff via `pr_diff`, not just the paste.\n" +
               "2. Flag only NEW blocking issues introduced by these commits — do NOT re-litigate " +
               "code you already approved unless the new commits broke it.\n\n" +
               "Respond with your assessment, then the verdict marker on its own line at the END of your reply:\n\n" +
               "REVIEWER_VERDICT: APPROVE | REQUEST_CHANGES\n\n" +
               "If REQUEST_CHANGES, precede it with a REVIEWER_NOTES: section listing the concrete " +
               "issues the engineer must fix (file + what to change). Be specific — these notes go " +
               "straight back to the engineer agent as rework instructions.";
    }

    private static (ReviewerVerdict Verdict, string Body) ParseReviewerOutput(string text)
    {
        var verdict = ReviewFindings.ParseVerdict(text);
        return (verdict, text);
    }
}

public enum ReviewerVerdict { Approve, RequestChanges, Error }
