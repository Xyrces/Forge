using Forge.Core;
using Octokit;
using Microsoft.Extensions.Logging;

namespace Forge.Reviewer;

public sealed partial class PRWatcher
{
    internal async Task<bool> HasOwnedWorkerAsync(string taskId, CancellationToken ct)
    {
        if (_hasDurableWorker is not null && await _hasDurableWorker(taskId, ct)) return true;
        return _runs is not null && (await _runs.ListActiveAsync(ct))
            .Any(run => run.TaskId == taskId && run.Role is "CoreDev" or "ClientDev");
    }

    private sealed record FormalGate(bool Approved, bool Waiting, IReadOnlyList<FormalDecision> Requests, bool AutomatedSatisfied,
        bool ProtectionUnavailable = false, bool HumanPending = false, string? MergeState = null, bool HumanWaiting = false,
        bool AutomationPending = false);

    private async Task<FormalGate> ReadFormalGateAsync(IssueRecord task, PullRequest pr, string head, CancellationToken ct)
    {
        if (_issues is not IssueStore store)
            throw new InvalidOperationException("Formal review requires the project's durable issue store.");
        var policy = new PullRequestReviewPolicy(new MemoryStore(store.Db));
        var settings = await policy.GetSettingsAsync(ct);
        var mode = settings.Mode;
        var hold = await policy.GetHoldAsync(task.Id, ct);
        if (hold.SyncPending)
        {
            await _gitHub.SetHumanReviewLabelAsync(pr.Number, hold.Required, ct);
            if (await _gitHub.HasHumanReviewLabelAsync(pr.Number, ct) != hold.Required)
                throw new InvalidOperationException("The human review label has not synchronized.");
            hold = hold with { SyncPending = false };
            await policy.SetHoldAsync(task.Id, hold, ct);
        }
        else
        {
            var label = await _gitHub.HasHumanReviewLabelAsync(pr.Number, ct);
            if (label != hold.Required)
            {
                hold = new(label, false);
                await policy.SetHoldAsync(task.Id, hold, "github-label", ct);
            }
        }
        var human = mode is RequiredReview.Human or RequiredReview.Both || hold.Required;
        var automated = mode is RequiredReview.Automated or RequiredReview.Both;
        if (automated && string.IsNullOrWhiteSpace(_gitHub.ReviewerLogin))
            throw new InvalidOperationException("Configure the independent Forge reviewer identity before automated review can approve merges.");
        var observedDecisions = await _gitHub.GetAuthorizedDecisionsAsync(pr.Number, pr.User?.Login, ct);
        var observedAt = await _gitHub.GetReviewObservationTimeAsync(pr.Number, head, ct);
        var authored = new List<VerifiedReviewOrigin>();
        if (task.GetMetadata("reviewHistory") is { } history)
        {
            var published = System.Text.Json.JsonSerializer.Deserialize<List<PublishedReview>>(history, ReviewFindings.Json)
                ?? throw new InvalidOperationException("Published review provenance is unreadable.");
            foreach (var review in published)
                if (string.Equals(review.Reviewer, _gitHub.ReviewerLogin, StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(review.Url, UriKind.Absolute, out var url) && url.Host == "github.com"
                    && url.AbsolutePath.Equals($"/{_gitHub.Owner}/{_gitHub.Repo}/pull/{pr.Number}", StringComparison.OrdinalIgnoreCase)
                    && url.Fragment.StartsWith("#pullrequestreview-", StringComparison.Ordinal)
                    && long.TryParse(url.Fragment["#pullrequestreview-".Length..], out var id))
                    authored.Add(new(id, review.Reviewer, review.HeadSha, review.SubmittedAt));
        }
        var origins = await new MemoryStore(store.Db).ObserveReviewOriginsAsync($"{_gitHub.Owner}/{_gitHub.Repo}/{observedAt.RepositoryId}/{observedAt.PullRequestId}", pr.Number,
            head, observedAt.ServerTime, observedDecisions.Select(d => new ReviewOriginObservation(d.Id, d.Login, d.Head, d.SubmittedAt)).ToArray(), authored, ct, observedAt.ResetMarker);
        var decisions = observedDecisions.Select(d => d with { Head = origins[d.Id] ?? "" }).ToArray();
        var effective = EffectiveReview.Evaluate(decisions, head, settings.NominatedHuman, pr.User?.Login);
        var latest = observedDecisions.GroupBy(d => d.Login, StringComparer.OrdinalIgnoreCase).Select(EffectiveReview.Latest);
        var humanProvenancePending = human && !effective.HumanApproved && latest.Any(d => !d.Automated
            && d.State == PullRequestReviewState.Approved && d.Head == head && origins[d.Id] != head
            && (string.IsNullOrWhiteSpace(settings.NominatedHuman) || string.Equals(d.Login, settings.NominatedHuman, StringComparison.OrdinalIgnoreCase)));
        var automatedProvenancePending = automated && !effective.AutomatedApproved && latest.Any(d => d.Automated
            && d.State == PullRequestReviewState.Approved && d.Head == head && origins[d.Id] != head);
        var requests = effective.CurrentRequests.Where(r => !r.Automated || automated).ToArray();
        ProtectionReviewDecision? protection = null;
        try { protection = await _gitHub.GetProtectionReviewDecisionAsync(pr.Number, head, ct); }
        catch (RateLimitExceededException) { throw; }
        catch (SecondaryRateLimitExceededException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A failed metadata read must not authorize a merge, but known CI failures,
            // conflicts and current-head review findings can still be repaired.
            _logger.LogWarning(ex, "Task {TaskId}: GitHub protected review decision unavailable", task.Id);
        }
        var protectionSatisfied = protection is ProtectionReviewDecision.NotRequired or ProtectionReviewDecision.Approved;
        MergeRequirements? merge = null;
        try { merge = await _gitHub.GetMergeRequirementsAsync(pr.Number, head, ct); }
        catch (RateLimitExceededException) { throw; }
        catch (SecondaryRateLimitExceededException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Task {TaskId}: GitHub merge requirements unavailable", task.Id); }
        var humanPending = effective.OutstandingHumanObjection || (human && !effective.HumanApproved);
        var mergeSatisfied = merge is { Unresolved: 0 } && (merge.AllowsMerge || merge.State is "BEHIND" or "DIRTY");
        HashSet<string> limitedConversations = [];
        if (task.GetMetadata("reviewConversationInputHead") == head)
        {
            try { limitedConversations = System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(task.GetMetadata("reviewConversationLimitedIds") ?? "[]") ?? []; }
            catch (System.Text.Json.JsonException ex) { _logger.LogWarning(ex, "Task {TaskId}: invalid retained conversation limit receipt", task.Id); }
        }
        var humanThreads = merge?.Conversations.Count(t => !t.Resolved
            && (!GitHubService.CanResolveConversation(t, _gitHub.ReviewerLogin) || limitedConversations.Contains(t.Id))) ?? 0;
        var humanWaiting = humanPending || protection is ProtectionReviewDecision.ReviewRequired or ProtectionReviewDecision.ChangesRequested || humanThreads > 0;
        var waiting = humanWaiting || !protectionSatisfied || !mergeSatisfied
            || task.GetMetadata("reviewMergeRefusedHead") == head;
        var fields = new Dictionary<string, object>
        {
            ["humanReviewNominee"] = settings.NominatedHuman ?? "",
            ["humanReviewRequired"] = human, ["humanReviewTaskHold"] = hold.Required,
            ["humanReviewSyncPending"] = false, ["reviewMode"] = mode.ToString(),
            ["reviewGateError"] = protection is null ? "GitHub’s protected review requirement could not be checked. Forge will retry the read; merging is paused." : "",
            ["reviewProtectionDecision"] = protection?.ToString() ?? "Unavailable",
            ["reviewProtectionHead"] = head,
            ["reviewHumanPending"] = humanPending,
            ["reviewProvenancePending"] = humanProvenancePending || automatedProvenancePending,
            ["reviewHumanProvenancePending"] = humanProvenancePending,
            ["reviewAutomatedProvenancePending"] = automatedProvenancePending,
            ["reviewMergeState"] = merge?.State ?? "Unavailable",
            ["reviewUnresolvedThreads"] = merge?.Unresolved ?? -1,
            ["reviewHumanThreads"] = humanThreads,
            ["reviewConversationInputLimited"] = merge?.Conversations.Count(t => !t.Resolved && limitedConversations.Contains(t.Id)) ?? 0
        };
        await store.PatchReviewMetadataAsync(task.Id, fields, null, ct);
        return new((!automated || effective.AutomatedApproved) && (!human || effective.HumanApproved)
            && !effective.OutstandingHumanObjection && protectionSatisfied && mergeSatisfied, waiting, requests,
            !automated || effective.AutomatedApproved, protection is null, humanPending, merge?.State, humanWaiting,
            merge is not null && merge.Unresolved > humanThreads);
    }

    private async Task RecordExternalWaitAsync(IssueRecord task, bool waiting, CancellationToken ct)
    {
        if (_issues is not IssueStore store) return;
        var current = await store.GetAsync(task.Id, ct) ?? task;
        var fields = ReviewWaitTiming.ObserveExternal(current, waiting, DateTimeOffset.UtcNow);
        if (fields.Count > 0) await store.PatchReviewMetadataAsync(task.Id, fields, null, ct);
    }

    private async Task RecordHumanWaitAsync(IssueRecord task, bool waiting, CancellationToken ct)
    {
        if (_issues is not IssueStore store) return;
        var current = await store.GetAsync(task.Id, ct) ?? task;
        var fields = ReviewWaitTiming.Observe(current, waiting, DateTimeOffset.UtcNow);
        fields["awaitingHumanReview"] = waiting;
        await store.PatchReviewMetadataAsync(task.Id, fields, null, ct);
    }

    private async Task<FormalGate?> TryReadFormalGateAsync(IssueRecord task, PullRequest pr, string head, CancellationToken ct)
    {
        try { return await ReadFormalGateAsync(task, pr, head, ct); }
        catch (RateLimitExceededException) { throw; } // The sweep owns the shared GitHub cooldown.
        catch (SecondaryRateLimitExceededException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Task {TaskId}: formal review gate unavailable; retaining watch", task.Id);
            if (_issues is IssueStore store)
                await store.PatchReviewMetadataAsync(task.Id, new Dictionary<string, object>
                { ["reviewGateError"] = ex.Message }, null, ct);
            return null;
        }
    }
}
