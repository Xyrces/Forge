namespace Forge.Core;

/// <summary>
/// The watchdog scanner (operator-approved v1, 2026-07-31):
/// alert-only structural-stall detection. Each check is a cheap
/// query producing findings; the finding store dedupes by
/// (kind, target) and auto-resolves when the condition clears.
/// The scheduler surfaces open findings on the Now attention feed.
/// </summary>
public static class WatchdogScanner
{
    public sealed record Finding(string Kind, string TargetId, string Severity, string Detail);

    public const string BlockedMemberStall = "blocked-member-stall";
    public const string StuckSprint = "stuck-sprint";
    public const string OperatorResidue = "operator-residue";
    public const string Starvation = "starvation";
    public const string DeadWatch = "dead-watch";
    public const string GroomerWedge = "groomer-wedge";

    private static readonly TimeSpan StuckSprintAfter = TimeSpan.FromDays(3);
    private static readonly TimeSpan ResidueAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan StarvationAfter = TimeSpan.FromHours(12);
    private static readonly TimeSpan DeadWatchAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan GroomerWedgeAfter = TimeSpan.FromHours(6);

    public static async Task<IReadOnlyList<Finding>> ScanAsync(
        IIssueStore issues, ISprintStore sprints, DateTime utcNow, CancellationToken ct = default)
    {
        var findings = new List<Finding>();
        var all = await issues.ListAsync(new IssueFilter(), ct);
        var byId = all.ToDictionary(i => i.Id);
        var active = await sprints.GetActiveAsync(ct);
        var memberIds = active is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(await sprints.GetIssueIdsAsync(active.Id, ct), StringComparer.Ordinal);
        var members = all.Where(i => memberIds.Contains(i.Id)
            && !AgentTaskTypes.IsContainer(i.Type)
            && i.Type != AgentTaskTypes.PrWatch).ToList();

        // 1. Sprint member with an OPEN blocker outside the sprint —
        // the task-348 stall class: undispatchable forever, and it
        // holds the sprint open.
        foreach (var m in members.Where(m => m.Status is IssueStatus.Pending or IssueStatus.InProgress))
        {
            if (!await issues.IsBlockedAsync(m.Id, ct)) continue;
            var openBlockers = await issues.OpenBlockersAsync(new[] { m.Id }, ct);
            if (openBlockers.TryGetValue(m.Id, out var blockers)
                && blockers.Any(b => !memberIds.Contains(b)))
            {
                findings.Add(new(BlockedMemberStall, m.Id, "fail",
                    $"{m.Id} is blocked by {string.Join(", ", blockers.Where(b => !memberIds.Contains(b)))} (not in the sprint) — it can never run and holds the sprint open"));
            }
        }

        // 2. Active sprint older than the bound — with its live
        // members' situation lines so the operator sees why at a
        // glance.
        if (active is not null && active.StartDate is { } start && utcNow - start > StuckSprintAfter)
        {
            var live = members
                .Where(m => m.Status is not (IssueStatus.Completed or IssueStatus.Closed or IssueStatus.Failed))
                .Select(m =>
                {
                    var s = TaskSituation.Describe(m);
                    return $"{m.Id} [{m.Status}]{(s.Text.Length > 0 ? $" — {s.Text}" : "")}";
                })
                .Take(6)
                .ToList();
            findings.Add(new(StuckSprint, active.Id, "warn",
                $"Active sprint '{active.Name}' is {(int)(utcNow - start).TotalDays}d old. Live members: {string.Join("; ", live)}"));
        }

        foreach (var i in all)
        {
            if (AgentTaskTypes.IsContainer(i.Type) || i.Type == AgentTaskTypes.PrWatch) continue;
            var age = utcNow - i.UpdatedAt;

            // 3. Blocked with no live blockers and no transient
            // marker — operator-decision residue aging out.
            if (i.Status == IssueStatus.Blocked
                && i.GetMetadata("blockedKind") is null
                && age > ResidueAfter
                && !await issues.IsBlockedAsync(i.Id, ct))
            {
                findings.Add(new(OperatorResidue, i.Id, "warn",
                    $"{i.Id} has been Blocked for {(int)age.TotalHours}h with no live blockers — clear strikes & requeue, or close: {i.Title}"));
            }

            // 6. Materialized follow-up waiting on grooming too long
            // (groomer down, or silently rejecting nothing).
            if (i.Status == IssueStatus.Pending
                && i.GetMetadata("followUpOf") is not null
                && i.GetMetadata("groomed") is null
                && age > GroomerWedgeAfter)
            {
                findings.Add(new(GroomerWedge, i.Id, "warn",
                    $"{i.Id} has awaited grooming for {(int)age.TotalHours}h — the next sprint is gated on this: {i.Title}"));
            }
        }

        foreach (var m in members)
        {
            var age = utcNow - m.UpdatedAt;

            // 4. Sprint member Pending+unassigned for half a day —
            // dispatch should have claimed it (slots full? crash?).
            if (m.Status == IssueStatus.Pending
                && m.Assignee is null
                && m.GetMetadata("prNumber") is null
                && age > StarvationAfter
                && !await issues.IsBlockedAsync(m.Id, ct))
            {
                findings.Add(new(Starvation, m.Id, "warn",
                    $"{m.Id} has sat unclaimed in the active sprint for {(int)age.TotalHours}h: {m.Title}"));
            }

            // 5. Watched PR with no movement: no verdict, no rework
            // round, no update in a day.
            if (m.Status is IssueStatus.Pending or IssueStatus.InProgress
                && m.GetMetadata("prNumber") is { } pr
                && m.GetMetadata("reviewVerdict") is null
                && m.GetMetadata("reworkForSha") is null
                && age > DeadWatchAfter)
            {
                findings.Add(new(DeadWatch, m.Id, "warn",
                    $"{m.Id} (PR #{pr}) has had no review, rework, or update for {(int)age.TotalHours}h: {m.Title}"));
            }
        }

        return findings;
    }
}
