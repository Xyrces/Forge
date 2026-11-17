using Forge.Agents;
using Forge.Core;

namespace Forge.Reviewer;

/// <summary>Caller-owned identity survives controller detachment; only a handled, joined result permits retry.</summary>
internal static class ReviewWorkerAttempt
{
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public static SemaphoreSlim Gate(string? project, string task, AgentType role) =>
        Locks[(StringComparer.Ordinal.GetHashCode($"{project}/{task}/{role}") & int.MaxValue) % Locks.Length];
    public static string Prefix(AgentType role) => role == AgentType.QA ? "qaWorker" : "reviewWorker";

    internal static IReadOnlyList<SemaphoreSlim> RetirementGates(string? project, string task) =>
        new[] { Gate(project, task, AgentType.QA), Gate(project, task, AgentType.Reviewer) }
            .Distinct().OrderBy(gate => Array.IndexOf(Locks, gate)).ToArray();

    public static async Task<(IssueRecord Task, ReviewWorkerState? Existing)> BeginAsync(IAgentRunner runner,
        IIssueStore issues, IssueRecord task, AgentType role, string? project, string head, CancellationToken ct)
    {
        if (runner is not IReviewWorkerLifecycle lifecycle) return (task, null);
        if (string.IsNullOrWhiteSpace(project)) throw new InvalidOperationException("Review worker requires an owning project.");
        var prefix = Prefix(role);
        var id = task.GetMetadata(prefix + "Id");
        if (id is not null)
        {
            var sameHead = task.GetMetadata(prefix + "Head") == head;
            var previous = await lifecycle.InspectReviewWorkerAsync(role, project, task.Id, id, !sameHead, ct);
            var consumed = task.GetMetadata(prefix + "Consumed") == id;
            if (sameHead && previous is not null && previous.Context.GetValueOrDefault("sourceSha") != head)
                throw new InvalidOperationException("Review worker source does not match the durable reviewed head.");
            if (sameHead && !consumed) return (task, previous);
            if (previous is { Terminal: false })
                throw new InvalidOperationException("Previous review worker has not terminated; refusing a new attempt.");
        }
        // Upgrade safety: old prompt-hash workers have no caller pointer. Never overlap them.
        if (await lifecycle.HasUntrackedReviewWorkerAsync(role, project, task.Id, ct))
            throw new InvalidOperationException("Untracked active review worker requires reconciliation before retry.");
        id = "review-" + Guid.NewGuid().ToString("N");
        task = await issues.TransitionAsync(task.Id, task.Status, "Review worker invocation prepared",
            new Dictionary<string, object> { [prefix + "Id"] = id, [prefix + "Head"] = head,
                [prefix + "Consumed"] = null! }, ct);
        return (task, null);
    }

    public static void Bind(IReadOnlyDictionary<string, string>? existing, IssueRecord task, AgentType role,
        string head, Dictionary<string, object> context)
    {
        var id = task.GetMetadata(Prefix(role) + "Id");
        if (id is null) return;
        context["dispatchId"] = id;
        context["sourceSha"] = head;
        if (existing is not null)
            foreach (var item in existing) context[item.Key] = item.Value;
    }

    public static async Task<string?> ConsumedAsync(IAgentRunner runner, IssueRecord task, AgentType role,
        string? project, CancellationToken ct)
    {
        if (runner is not IReviewWorkerLifecycle lifecycle) return null;
        var id = task.GetMetadata(Prefix(role) + "Id");
        if (id is null) return null;
        var state = await lifecycle.InspectReviewWorkerAsync(role, project!, task.Id, id, false, ct);
        return state is null or { Terminal: true } ? id : null;
    }
}
