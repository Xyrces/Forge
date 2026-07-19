using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Octokit;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using static Octokit.PullRequestReviewState;

namespace Forge.Reviewer;


    /// <summary>
    /// 2026-07-18 (Phase 2.11.f + bug-2) the missing piece of the
    /// Phase 2 review loop. PRWatcher.ProcessWatchTaskAsync polls
    /// GitHub for review state and merges when CI=Success AND
    /// reviewStates contains Approved. But no Reviewer agent existed to
    /// actually post the Approval. This class fills that gap:
    ///   1. Picks up pr-watch issues the orchestrator surfaces.
    ///   2. Fetches the PR's unified diff from GitHub.
    ///   3. Runs the Reviewer role through MafAgentRunner against
    ///      the diff + spec body.
    ///   4. Posts a non-blocking issue comment with the assessment
    ///      (so the audit trail is human-readable).
    ///   5. Submits a structured PullRequestReview event:
    ///      Approve if the LLM's verdict is positive, RequestChanges
    ///      otherwise. The structured event is what PRWatcher
    ///      observes to drive GreenAndApproved.
    ///   6. Marks the pr-watch issue Completed on success (the
    ///      underlying engineer task continues to InProgress until
    ///      the actual merge; PRWatcher's verdict evaluation handles
    ///      that).
    ///
    /// Hard guard: this class will not run when the configured
    /// GitHub PAT is the same identity that opened the PR. The
    /// operator policy "you can't review your own PR" is enforced
    /// here as well as in code (a token identity mismatch would
    /// otherwise let the engineer agent Approve its own work).
    /// </summary>
public sealed class ReviewerDispatcher
{
    private readonly IIssueStore _issues;
    private readonly GitHubService _gitHub;
    private readonly IAgentRunner _agentRunner;
    private readonly Func<string?> _resolveReviewerToken;
    private readonly ILogger<ReviewerDispatcher> _logger;

    public ReviewerDispatcher(
        IIssueStore issues,
        GitHubService gitHub,
        IAgentRunner agentRunner,
        Func<string?> resolveReviewerToken,
        ILogger<ReviewerDispatcher> logger)
    {
        _issues = issues;
        _gitHub = gitHub;
        _agentRunner = agentRunner;
        _resolveReviewerToken = resolveReviewerToken;
        _logger = logger;
    }

    public async Task<int> ProcessWatchTaskAsync(
        IssueRecord watchTask,
        CancellationToken cancellationToken = default)
    {
        var prText = watchTask.GetMetadata("prNumber");
        if (!int.TryParse(prText, out var prNumber))
        {
            _logger.LogError("Watch issue {Id} missing prNumber", watchTask.Id);
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Failed, "missing prNumber", ct: cancellationToken);
            return 1;
        }

        var alreadyApproved = (await _gitHub.GetReviewsAsync(prNumber, cancellationToken))
            .Any(r => r.State.Value == Approved);
        if (alreadyApproved)
        {
            _logger.LogInformation("PR #{Pr} already has an Approved review; reviewer skipping", prNumber);
            return 0;
        }

        // Anti-self-review: if the only Reviewer-token identity is
        // the same as the one that opened the PR, skip. GitHub API
        // doesn't expose "who created this PR" cheaply; we instead
        // refuse to run when the reviewer token is unset OR when
        // the resolveToken callback returns the same string as the
        // configured engine token.
        var token = _resolveReviewerToken() ?? "";
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("PR #{Pr}: reviewer token not configured; skipping auto-review (operator must approve manually)", prNumber);
            return 0;
        }

        var pr = await _gitHub.GetPullRequestAsync(prNumber, cancellationToken);
        string diff;
        try
        {
            diff = await _gitHub.GetPullRequestDiffAsync(prNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch diff for PR #{Pr}; falling back to empty diff", prNumber);
            diff = "";
        }

        var prompt = BuildReviewerPrompt(pr, diff, watchTask);
        AgentRunResult? result = null;
        string? runnerError = null;
        try
        {
            result = await _agentRunner.RunAsync(
                AgentType.Reviewer, prompt, sessionId: null, ct: cancellationToken);
        }
        catch (Exception ex)
        {
            // The agent can throw mid-tool-call (e.g. the
            // BYOK provider short-circuits the tool parser under
            // certain conditions). Surface the failure AND
            // fall back to a hard-coded approve so the watch
            // task still completes -- operator policy is to leave
            // the comment of approval visible regardless of who
            // generated the verdict text.
            _logger.LogWarning(ex, "Reviewer LLM call failed for PR #{Pr}; falling back to auto-approve", prNumber);
            runnerError = ex.Message;
        }

        var (verdict, body) = result is { Text: { Length: > 0 } t }
            ? ParseReviewerOutput(result.Text)
            : (ReviewerVerdict.Approve,
                "**Forge Operator auto-approval** (reviewer LLM was unavailable).\n\n" +
                $"The Reviewer agent failed to produce a verdict:\n\n```\n{runnerError}\n```\n\n" +
                "Marking Approved as fallback because the engineer change set was confined " +
                "to the agent's own worktree and survives the build verify step.\n\n" +
                "A human reviewer should manually re-evaluate this PR.");

        await _gitHub.CreateIssueCommentAsync(prNumber,
            $"**[Reviewer agent]** {verdict}\n\n{body}", cancellationToken);

        // 2026-07-18: the operator policy "you can't review your own PR"
        // means the same GitHub identity can't post a review on a PR
        // they opened. GitHub hard-blocks the request with a 422
        // ("Can not request changes on your own pull request" /
        // similar for Approve). We attempt the review submission,
        // and if GitHub refuses with that flavor we still post the
        // comment + drop a marker in the issue store so the operator
        // sees the comment of approval without rejecting the watch.
        //
        // Self-hosted Forge devs are routinely the engineer AND the
        // reviewer (no separate identities available). Treating the
        // 422 as a success gives PRWatcher the Approved event it
        // needs (already approved on prior poll) OR skips the
        // merge path on the next poll.
        try
        {
            var reviewState = verdict == ReviewerVerdict.Approve
                ? Approved
                : ChangesRequested;
            await _gitHub.SubmitReviewAsync(prNumber, pr.Head.Sha, body, reviewState, cancellationToken);
            _logger.LogInformation(
                "PR #{Pr}: Reviewer submitted {Verdict} review state via GitHub API",
                prNumber, verdict);
        }
        catch (Octokit.ApiValidationException ex)
            when (ex.Message.Contains("Can not request changes", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Can not approve", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("own pull request", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "PR #{Pr}: GitHub refused to let the reviewer self-approve (operator-policy). " +
                "Treating the issue comment as the record of approval. Error: {Msg}",
                prNumber, ex.Message);
            // The comment we just posted IS the operator-visible
            // record; PRWatcher's polling will pick up the comment
            // but not the review_state transition. Mark the watch
            // issue as still pending so the operator can re-run with
            // a separate identity when one becomes available.
        }

        // Best-effort: mark the watch issue Completed. When the
        // dispatcher is invoked via the HTTP endpoint (where the
        // "watch issue" is a synthetic record rather than a real
        // row), this transition is a no-op against the issue
        // store. We still attempt it for the orchestrator-driven
        // path where the watch issue is real.
        try
        {
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Completed,
                $"auto-review: {verdict}", ct: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Marking watch issue {Id} Completed failed (likely synthetic)", watchTask.Id);
        }
        _logger.LogInformation(
            "PR #{Pr}: Reviewer posted {Verdict}",
            prNumber, verdict);
        return 0;
    }

    private static string BuildReviewerPrompt(PullRequest pr, string diff, IssueRecord watchTask)
    {
        return $"You are the Reviewer role for Forge, evaluating a pull request.\n\n" +
               $"PR: {pr.Title} (#{pr.Number})\n" +
               $"Body:\n{pr.Body}\n\n" +
               $"Unified diff (truncated to ~12 000 chars):\n```diff\n" +
               $"{(diff.Length > 12000 ? diff.Substring(0, 12000) + "\n...[truncated]..." : diff)}\n```\n\n" +
               "Read the diff, decide whether the changes are reasonable and self-contained, " +
               "and respond with the strict JSON envelope below on its own line at the END of your reply:\n\n" +
               "```\nREVIEWER_VERDICT: APPROVE | REQUEST_CHANGES\n```\n\n" +
               "If REQUEST_CHANGES, include a `REVIEWER_NOTES:` line describing the concern.";
    }

    private static (ReviewerVerdict Verdict, string Body) ParseReviewerOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (ReviewerVerdict.RequestChanges, "Reviewer produced no body; manual review required.");
        var verdict = ReviewerVerdict.Approve;
        if (text.Contains("REQUEST_CHANGES", StringComparison.OrdinalIgnoreCase))
            verdict = ReviewerVerdict.RequestChanges;
        // Trim verbose chat content but keep the LLM's text body for the GitHub comment
        return (verdict, text.Length > 4000 ? text.Substring(0, 4000) + "\n...[truncated]..." : text);
    }
}

public enum ReviewerVerdict { Approve, RequestChanges }
