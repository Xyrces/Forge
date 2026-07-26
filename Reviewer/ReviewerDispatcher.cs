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
    private readonly ILogger<ReviewerDispatcher> _logger;

    public ReviewerDispatcher(
        IIssueStore issues,
        GitHubService gitHub,
        IAgentRunner agentRunner,
        ILogger<ReviewerDispatcher> logger)
    {
        _issues = issues;
        _gitHub = gitHub;
        _agentRunner = agentRunner;
        _logger = logger;
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
        IssueRecord watchTask, CancellationToken cancellationToken = default,
        Func<PullRequest, string>? headShaOverride = null)
    {
        var prText = watchTask.GetMetadata("prNumber");
        if (!int.TryParse(prText, out var prNumber))
        {
            _logger.LogError("Watch issue {Id} missing prNumber", watchTask.Id);
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
        var reviewedSha = watchTask.GetMetadata("reviewSha");
        var recordedVerdict = watchTask.GetMetadata("reviewVerdict");
        if (string.Equals(reviewedSha, headSha, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(recordedVerdict)
            && recordedVerdict != nameof(ReviewerVerdict.Error))
        {
            return null;
        }

        string diff;
        try
        {
            diff = await _gitHub.GetPullRequestDiffAsync(prNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch diff for PR #{Pr}; reviewing with empty diff", prNumber);
            diff = "";
        }

        var round = 1;
        if (int.TryParse(watchTask.GetMetadata("reviewRound"), out var prior)) round = prior + 1;

        var prompt = BuildReviewerPrompt(pr, diff, watchTask);
        ReviewerVerdict verdict;
        string body;
        string? error = null;
        try
        {
            // Context carries the watched task id so the Reviewer's
            // file_followup tool can defer non-blocking findings as
            // groomable follow-up tasks (parented via metadata).
            var reviewContext = new Dictionary<string, object>
            {
                ["issueId"] = watchTask.GetMetadata("taskId") ?? watchTask.Id,
            };
            var result = await _agentRunner.RunAsync(
                AgentType.Reviewer, prompt, sessionId: null, context: reviewContext, ct: cancellationToken);
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

        // Record the verdict in the watch metadata — the PRWatcher's
        // merge/rework decision reads this.
        await UpdateWatchMetadataAsync(watchTask, m =>
        {
            m["reviewSha"] = headSha;
            m["reviewVerdict"] = verdict.ToString();
            m["reviewNotes"] = body.Length > 2000 ? body[..2000] : body;
            m["reviewRound"] = round;
            if (error is not null) m["reviewError"] = error;
            else m.Remove("reviewError");
            return m;
        }, cancellationToken);

        _logger.LogInformation("PR #{Pr}: reviewer verdict {Verdict} (round {Round}, sha {Sha})",
            prNumber, verdict, round, headSha[..Math.Min(7, headSha.Length)]);
        return new ReviewOutcome(verdict, body, headSha, error);
    }

    /// <summary>
    /// Back-compat wrapper for the HTTP endpoint: review once and
    /// return a process-style exit code.
    /// </summary>
    public async Task<int> ProcessWatchTaskAsync(
        IssueRecord watchTask,
        CancellationToken cancellationToken = default)
    {
        var outcome = await ReviewOnceAsync(watchTask, cancellationToken);
        return outcome is null || outcome.Error is null ? 0 : 1;
    }

    private async Task UpdateWatchMetadataAsync(
        IssueRecord watchTask,
        Func<Dictionary<string, object>, Dictionary<string, object>> mutate,
        CancellationToken ct)
    {
        var cur = await _issues.GetAsync(watchTask.Id, ct);
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

    private static string BuildReviewerPrompt(PullRequest pr, string diff, IssueRecord watchTask)
    {
        var taskTitle = watchTask.GetMetadata("taskTitle") ?? watchTask.Title;
        return $"You are the Reviewer role for Forge, evaluating a pull request against its task.\n\n" +
               $"Task: {taskTitle}\n" +
               $"PR: {pr.Title} (#{pr.Number})\n" +
               $"Body:\n{pr.Body}\n\n" +
               $"Unified diff (truncated to ~12 000 chars):\n```diff\n" +
               $"{(diff.Length > 12000 ? diff.Substring(0, 12000) + "\n...[truncated]..." : diff)}\n```\n\n" +
               "Check that the changes implement the task, are self-contained, follow the repo's " +
               "conventions, and don't introduce dead code, unrelated rewrites, or artifacts that " +
               "don't belong in version control. Respond with your assessment, then the verdict " +
               "marker on its own line at the END of your reply:\n\n" +
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
