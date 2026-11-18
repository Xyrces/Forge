using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class IssueStore
{
    /// <summary>Called only after sprint/dependency/admission checks, before competing for a worker slot.</summary>
    public async Task ObserveDispatchEligibleAsync(IssueRecord task, string activeSprintId, CancellationToken ct)
    {
        var current = await GetAsync(task.Id, ct);
        if (current is null || current.Status != IssueStatus.Pending) return;
        // The latest durable queue transition is attributable; creation and mutable
        // UpdatedAt are not eligibility evidence. This interval can include holds.
        await using var connection = await Db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Db.Dialect.Top(1)}id,ts,detail,kind FROM {T("issue_event")} WHERE issue_id=@id AND (kind='status_change' OR (kind='sprint_activated' AND detail=@sprint)) ORDER BY id DESC{Db.Dialect.Limit(1)}";
        command.AddParam("@id", task.Id);
        command.AddParam("@sprint", activeSprintId);
        DispatchWakeupTiming? timing = null;
        var observed = DateTimeOffset.UtcNow;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct) && !reader.IsDBNull(2))
            {
                var detail = reader.GetString(2);
                var kind = reader.GetString(3);
                var transition = detail.Split(' ', 2)[0].Split("->");
                if (kind == "sprint_activated" || (transition.Length == 2 && transition[0] != "Pending" && transition[1] == "Pending"))
                {
                    var at = new DateTimeOffset(DateTime.SpecifyKind(ParseTime(reader.GetString(1)), DateTimeKind.Utc));
                    if (at <= observed)
                        timing = new(kind == "sprint_activated" ? "active-sprint-selection" : "task-status-returned-to-pending", reader.GetInt64(0), kind, detail, at, observed);
                }
            }
        }
        var key = string.Join("|", activeSprintId, current.GetMetadata("reworkForSha"),
            current.GetMetadata("reworkAttempts"), current.GetMetadata("requeuedFromFailedAt"), timing?.EventId.ToString());
        if (current.GetMetadata("dispatchEligibilityKey") == key) return;
        // A changed sprint/rework episode must not recycle an already observed event.
        if (timing is not null && DateTimeOffset.TryParse(current.GetMetadata("dispatchEligibleAt"), out var previous)
            && timing.EventAt <= previous) timing = null;
        await PatchMetadataOnlyAsync(task.Id, new Dictionary<string, object>
        {
            ["dispatchEligibilityKey"] = key,
            ["dispatchEligibleAt"] = observed.ToString("O"),
            ["dispatchWakeup"] = timing!
        }, null, ct);
    }
}
