using Forge.Core.Db;

namespace Forge.Core;

public sealed record SprintPlanningCandidate(string Id, string Title, int Priority, string? BindingJson);

/// <summary>Bounded planning projection: never materializes full project rows or transcripts.</summary>
public sealed class SprintPlanningStore(IssueStore issues)
{
    public async Task<IReadOnlyList<SprintPlanningCandidate>> CandidatesAsync(int limit, string? outcomeId = null, CancellationToken ct = default, string? afterId = null)
    {
        limit = Math.Clamp(limit, 1, 1001);
        var d = issues.Db.Dialect;
        var json = issues.Db.Provider == ForgeDbProvider.SqlServer
            ? "JSON_QUERY(metadata_json,'$.sprintOutcome')" : "json_extract(metadata_json,'$.sprintOutcome')";
        var outcome = issues.Db.Provider == ForgeDbProvider.SqlServer
            ? "JSON_VALUE(metadata_json,'$.sprintOutcome.outcomeId')" : "json_extract(metadata_json,'$.sprintOutcome.outcomeId')";
        var bounded = issues.Db.Provider == ForgeDbProvider.SqlServer ? $"LEFT({json},32769)" : $"substr({json},1,32769)";
        await using var connection = await issues.Db.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {d.Top(limit)}id,title,priority,{bounded} FROM {d.Table("issue")} i WHERE status='Pending' AND type NOT IN ('epic','story','pr-watch')"
            + (outcomeId is null ? "" : $" AND {outcome}=@outcome")
            + (afterId is null ? "" : $" AND NOT EXISTS(SELECT 1 FROM {d.Table("issue")} cursor_row WHERE cursor_row.id=@after AND (i.priority<cursor_row.priority OR (i.priority=cursor_row.priority AND (i.created_at<cursor_row.created_at OR (i.created_at=cursor_row.created_at AND i.id<=cursor_row.id)))))")
            + $" ORDER BY priority,created_at,id{d.Limit(limit)}";
        if (outcomeId is not null) cmd.AddParam("@outcome", outcomeId);
        if (afterId is not null) cmd.AddParam("@after", afterId);
        var rows = new List<SprintPlanningCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }
}
