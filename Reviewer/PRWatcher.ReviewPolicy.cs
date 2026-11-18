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

    private sealed record FormalGate(bool Approved, bool Waiting, IReadOnlyList<FormalDecision> Requests, bool AutomatedSatisfied, bool ProtectionUnavailable = false, bool HumanPending = false);

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
        var effective = EffectiveReview.Evaluate(await _gitHub.GetAuthorizedDecisionsAsync(pr.Number, pr.User?.Login, ct), head, settings.NominatedHuman, pr.User?.Login);
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
        var humanPending = effective.OutstandingHumanObjection || (human && !effective.HumanApproved);
        var waiting = humanPending || !protectionSatisfied;
        var fields = new Dictionary<string, object>
        {
            ["humanReviewNominee"] = settings.NominatedHuman ?? "",
            ["humanReviewRequired"] = human, ["humanReviewTaskHold"] = hold.Required,
            ["humanReviewSyncPending"] = false, ["reviewMode"] = mode.ToString(),
            ["reviewGateError"] = protection is null ? "GitHub’s protected review requirement could not be checked. Forge will retry the read; merging is paused." : "",
            ["reviewProtectionDecision"] = protection?.ToString() ?? "Unavailable",
            ["reviewProtectionHead"] = head,
            ["reviewHumanPending"] = humanPending
        };
        await store.PatchReviewMetadataAsync(task.Id, fields, null, ct);
        return new((!automated || effective.AutomatedApproved) && (!human || effective.HumanApproved)
            && !effective.OutstandingHumanObjection && protectionSatisfied, waiting, requests,
            !automated || effective.AutomatedApproved, protection is null, humanPending);
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
