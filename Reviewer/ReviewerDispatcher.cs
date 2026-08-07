using Microsoft.Extensions.Logging;
using Octokit;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using static Octokit.PullRequestReviewState;

namespace Forge.Reviewer;

/// <summary>
/// The reviewer stage of the sprint review loop. For a pr-watch
/// issue: fetch the PR's diff, run the Reviewer role against it,
/// post the assessment as a GitHub comment (human-readable audit),
/// and record the structured verdict in the watch's metadata —
/// <c>reviewSha</c>, <c>reviewVerdict</c> (Approved |
/// ChangesRequested | Error), <c>reviewNotes</c>, <c>reviewRound</c>.
///
/// <para>
/// The queue metadata is the machine record the PRWatcher merges
/// on; the GitHub comment is the audit trail. A formal GitHub
/// review submission is attempted opportunistically (works when the
/// engine identity differs from the PR author; GitHub hard-blocks
/// formal self-reviews with a 422, which is tolerated — the local
/// verdict is authoritative in the solo-identity model).
/// </para>
///
/// <para>
/// Reviews are per-head-SHA: a watch whose PR head moved since the
/// last review gets re-reviewed (rework round). The watcher owns
/// all issue transitions; this class only writes verdict metadata.
/// </para>
/// </summary>
public sealed class ReviewerDispatcher
{
    private readonly IIssueStore _issues;
    private readonly GitHubService _gitHub;
    private readonly IAgentRunner _agentRunner;
    private readonly Forge.Core.TaskStateMachine? _lifecycle;
    private readonly IDashboardEventBus? _events;
    private readonly string? _projectId;

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
    private static readonly TimeSpan ReviewRunTimeout = TimeSpan.FromMinutes(12);
    private readonly ILogger<ReviewerDispatcher> _logger;

    public ReviewerDispatcher(
        IIssueStore issues,
        GitHubService gitHub,
        IAgentRunner agentRunner,
        ILogger<ReviewerDispatcher> logger,
        Forge.Core.TaskStateMachine? lifecycle = null,
        IDashboardEventBus? events = null,
        string? projectId = null)
    {
        _issues = issues;
        _gitHub = gitHub;
        _agentRunner = agentRunner;
        _logger = logger;
        _lifecycle = lifecycle;
        _events = events;
        _projectId = projectId;
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

        // Per-SHA dedupe: the watch sweep calls this every pass; only
        // a head move (rework push) triggers a fresh round. An Error
        // verdict does NOT dedupe — the sweep retries the review (the
        // watcher's circuit breaker bounds the retries).
        var reviewedSha = task.GetMetadata("reviewSha");
        var recordedVerdict = task.GetMetadata("reviewVerdict");
        if (string.Equals(reviewedSha, headSha, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(recordedVerdict)
            && recordedVerdict != nameof(ReviewerVerdict.Error))
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

        // Worktree access is MANDATORY (operator rule 2026-07-30): the
        // reviewer cannot do its job from a diff paste — it needs the
        // branch checkout for full context. No usable worktree →
        // Error, retried next sweep; never a degraded paste-only
        // review.
        var worktreePath = task.GetMetadata("worktreePath");
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            _logger.LogWarning(
                "PR #{Pr} (task {Id}): no usable worktree (path={Path}) — reviewer requires workspace access; retrying next sweep",
                prNumber, task.Id, worktreePath ?? "<none>");
            return new ReviewOutcome(ReviewerVerdict.Error, "", headSha, "worktree missing");
        }
        // Fresh refs so `git show <sha>` / `git diff origin/<base>...<sha>`
        // resolve even when the checkout lags the head (external push).
        await TryFetchAsync(worktreePath, cancellationToken);

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

        // Audit trail on GitHub (comments are allowed on own PRs;
        // only formal reviews are identity-restricted).
        if (verdict != ReviewerVerdict.Error)
        {
            try
            {
                await _gitHub.CreateIssueCommentAsync(prNumber,
                    $"**[Forge Reviewer — round {round}]** {verdict}\n\n{body}", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PR #{Pr}: review comment post failed", prNumber);
            }
            // Opportunistic formal review: works when the engine
            // identity differs from the PR author; the solo-identity
            // 422 is expected and tolerated (local verdict rules).
            try
            {
                await _gitHub.SubmitReviewAsync(prNumber, headSha, body,
                    verdict == ReviewerVerdict.Approve ? Approved : ChangesRequested,
                    cancellationToken);
            }
            catch (Octokit.ApiValidationException ex)
                when (ex.Message.Contains("own pull request", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("Can not request changes", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("Can not approve", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("PR #{Pr}: formal self-review blocked (expected solo-identity 422)", prNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PR #{Pr}: formal review submit failed", prNumber);
            }
        }

        // Record the verdict in the task metadata — the PRWatcher's
        // merge/rework decision reads this. The verdict landing
        // clears the "reviewing…" marker (metadata is upsert-merge
        // only — JSON null is the delete idiom).
        await UpdateWatchMetadataAsync(task, m =>
        {
            m["reviewSha"] = headSha;
            m["reviewVerdict"] = verdict.ToString();
            m["reviewNotes"] = body.Length > 2000 ? body[..2000] : body;
            m["reviewRound"] = round;
            m["reviewStartedAt"] = null!;
            if (error is not null) m["reviewError"] = error;
            else m.Remove("reviewError");
            return m;
        }, cancellationToken);

        _logger.LogInformation("PR #{Pr}: reviewer verdict {Verdict} (round {Round}, sha {Sha})",
            prNumber, verdict, round, headSha[..Math.Min(7, headSha.Length)]);

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
        if (cur is null) return;
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
            catch { /* malformed metadata: start fresh */ }
        }
        var next = mutate(current);
        await _issues.TransitionAsync(cur.Id, cur.Status, error: null, metadata: next, ct: ct);
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
        if (string.IsNullOrEmpty(text))
            return (ReviewerVerdict.Error, "Reviewer produced no output.");
        var verdict = ReviewerVerdict.Approve;
        if (text.Contains("REQUEST_CHANGES", StringComparison.OrdinalIgnoreCase))
            verdict = ReviewerVerdict.RequestChanges;
        return (verdict, text.Length > 4000 ? text.Substring(0, 4000) + "\n...[truncated]..." : text);
    }
}

public enum ReviewerVerdict { Approve, RequestChanges, Error }
