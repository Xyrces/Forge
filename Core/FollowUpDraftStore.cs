namespace Forge.Core;

/// <summary>Follow-up draft: tracked work discovered during a sprint,
/// NOT yet a task (operator model 2026-07-31: no follow-up issues
/// against unmerged work — drafts materialize at sprint
/// completion).</summary>
public sealed record FollowUpDraft(
    long Id,
    string? SprintId,
    string SourceIssueId,
    string SourceRole,
    string Title,
    string Description,
    int Priority,
    string? BlocksIssueId,
    DateTime CreatedAt,
    DateTime? ConsumedAt,
    string? Disposition = null,
    string? DispositionDetail = null);

/// <summary>
/// Per-project store for follow-up drafts. Follow-ups filed mid-
/// sprint are tracked here; when the sprint completes, the assembler
/// materializes unconsumed drafts into real tasks (which then go
/// through grooming before the next sprint assembles).
/// </summary>
public sealed class FollowUpDraftStore
{
    private readonly IssueStore _issues;

    public FollowUpDraftStore(IssueStore issues)
    {
        _issues = issues;
    }

    private string T(string table) => _issues.Db.Dialect.Table(table);

    public async Task<long> FileAsync(FollowUpDraft draft, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _issues.Db.Provider == Db.ForgeDbProvider.SqlServer
            ? $"""
                INSERT INTO {T("followup_draft")} (sprint_id, source_issue_id, source_role, title, description, priority, blocks_issue_id, created_at)
                OUTPUT INSERTED.id
                VALUES (@sprint, @source, @role, @title, @desc, @pri, @blocks, @now);
                """
            : $"""
                INSERT INTO {T("followup_draft")} (sprint_id, source_issue_id, source_role, title, description, priority, blocks_issue_id, created_at)
                VALUES ($sprint, $source, $role, $title, $desc, $pri, $blocks, $now);
                SELECT last_insert_rowid();
                """;
        void Add(string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        Add("@sprint", draft.SprintId); Add("$sprint", draft.SprintId);
        Add("@source", draft.SourceIssueId); Add("$source", draft.SourceIssueId);
        Add("@role", draft.SourceRole); Add("$role", draft.SourceRole);
        Add("@title", draft.Title); Add("$title", draft.Title);
        Add("@desc", draft.Description); Add("$desc", draft.Description);
        Add("@pri", draft.Priority); Add("$pri", draft.Priority);
        Add("@blocks", draft.BlocksIssueId); Add("$blocks", draft.BlocksIssueId);
        var now = DateTime.UtcNow.ToString(IssueStore.DateFormat);
        Add("@now", now); Add("$now", now);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        // Publish AFTER the mutation commits; the publisher swallows
        // failures (a hint never breaks a DB mutation).
        await _issues.Events.PublishAsync(new Messaging.FollowUpFiled
        {
            MessageId = Messaging.FollowUpFiled.IdFor(id),
            ProjectId = _issues.ProjectId,
            FollowUpId = id,
            FollowUpOfTaskId = draft.SourceIssueId,
            Title = draft.Title,
        }, ct);
        return id;
    }

    /// <summary>All unconsumed drafts, oldest first (materialization
    /// candidates at sprint completion).</summary>
    public async Task<IReadOnlyList<FollowUpDraft>> ListUnconsumedAsync(CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, sprint_id, source_issue_id, source_role, title, description, priority, blocks_issue_id, created_at, consumed_at, disposition, disposition_detail
            FROM {T("followup_draft")}
            WHERE consumed_at IS NULL
            ORDER BY id
            """;
        var list = new List<FollowUpDraft>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Map(rd));
        return list;
    }

    /// <summary>One draft by id (any state — dispositions are read
    /// from consumed drafts too).</summary>
    public async Task<FollowUpDraft?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _issues.Db.Provider == Db.ForgeDbProvider.SqlServer
            ? $"""
                SELECT id, sprint_id, source_issue_id, source_role, title, description, priority, blocks_issue_id, created_at, consumed_at, disposition, disposition_detail
                FROM {T("followup_draft")} WHERE id = @id
                """
            : $"""
                SELECT id, sprint_id, source_issue_id, source_role, title, description, priority, blocks_issue_id, created_at, consumed_at, disposition, disposition_detail
                FROM {T("followup_draft")} WHERE id = $id
                """;
        var p = cmd.CreateParameter();
        p.ParameterName = _issues.Db.Provider == Db.ForgeDbProvider.SqlServer ? "@id" : "$id";
        p.Value = id;
        cmd.Parameters.Add(p);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Map(rd) : null;
    }

    /// <summary>Unconsumed drafts for a sprint (dashboard view).</summary>
    public async Task<IReadOnlyList<FollowUpDraft>> ListOpenForSprintAsync(string sprintId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _issues.Db.Provider == Db.ForgeDbProvider.SqlServer
            ? $"""
                SELECT id, sprint_id, source_issue_id, source_role, title, description, priority, blocks_issue_id, created_at, consumed_at, disposition, disposition_detail
                FROM {T("followup_draft")} WHERE consumed_at IS NULL AND sprint_id = @sid ORDER BY id
                """
            : $"""
                SELECT id, sprint_id, source_issue_id, source_role, title, description, priority, blocks_issue_id, created_at, consumed_at, disposition, disposition_detail
                FROM {T("followup_draft")} WHERE consumed_at IS NULL AND sprint_id = $sid ORDER BY id
                """;
        var p = cmd.CreateParameter();
        p.ParameterName = _issues.Db.Provider == Db.ForgeDbProvider.SqlServer ? "@sid" : "$sid";
        p.Value = sprintId;
        cmd.Parameters.Add(p);
        var list = new List<FollowUpDraft>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Map(rd));
        return list;
    }

    public async Task ConsumeAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        await using var conn = await _issues.Db.OpenAsync(ct);
        var names = idList.Select((_, i) => _issues.Db.Provider == Db.ForgeDbProvider.SqlServer ? $"@id{i}" : $"$id{i}").ToArray();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {T("followup_draft")} SET consumed_at = "
            + (_issues.Db.Provider == Db.ForgeDbProvider.SqlServer ? "@now" : "$now")
            + $" WHERE id IN ({string.Join(",", names)})";
        var now = cmd.CreateParameter();
        now.ParameterName = _issues.Db.Provider == Db.ForgeDbProvider.SqlServer ? "@now" : "$now";
        now.Value = DateTime.UtcNow.ToString(IssueStore.DateFormat);
        cmd.Parameters.Add(now);
        for (var i = 0; i < idList.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = names[i];
            p.Value = idList[i];
            cmd.Parameters.Add(p);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Record the triage outcome for a draft (materialized /
    /// merged / epic / discarded + the target id or reason). Sets
    /// consumed_at at the same time.</summary>
    public async Task SetDispositionAsync(long id, string disposition, string? detail, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sql = _issues.Db.Provider == Db.ForgeDbProvider.SqlServer
            ? $"UPDATE {T("followup_draft")} SET disposition = @d, disposition_detail = @dd, consumed_at = @now WHERE id = @id"
            : $"UPDATE {T("followup_draft")} SET disposition = $d, disposition_detail = $dd, consumed_at = $now WHERE id = $id";
        cmd.CommandText = sql;
        void Add(string n, object? v) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v ?? DBNull.Value; cmd.Parameters.Add(p); }
        var px = _issues.Db.Provider == Db.ForgeDbProvider.SqlServer ? "@" : "$";
        Add(px + "d", disposition); Add(px + "dd", detail);
        Add(px + "now", DateTime.UtcNow.ToString(IssueStore.DateFormat)); Add(px + "id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static FollowUpDraft Map(System.Data.Common.DbDataReader rd) =>
        new(Id: Convert.ToInt64(rd.GetValue(0)),
            SprintId: rd.IsDBNull(1) ? null : rd.GetString(1),
            SourceIssueId: rd.GetString(2),
            SourceRole: rd.GetString(3),
            Title: rd.GetString(4),
            Description: rd.GetString(5),
            Priority: rd.GetInt32(6),
            BlocksIssueId: rd.IsDBNull(7) ? null : rd.GetString(7),
            CreatedAt: IssueStore.ParseTime(rd.GetString(8)),
            ConsumedAt: rd.IsDBNull(9) ? null : IssueStore.ParseTime(rd.GetString(9)),
            Disposition: rd.FieldCount > 10 && !rd.IsDBNull(10) ? rd.GetString(10) : null,
            DispositionDetail: rd.FieldCount > 11 && !rd.IsDBNull(11) ? rd.GetString(11) : null);
}
