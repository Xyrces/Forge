using System.Data.Common;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;

namespace Forge.Core;

public enum SprintStatus { Active, Completed, Archived }

public sealed record SprintRecord(
    string Id, string Name, string Goal,
    DateTime StartDate, DateTime EndDate,
    SprintStatus Status,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record NewSprint(
    string Name, string Goal,
    DateTime StartDate, DateTime EndDate,
    SprintStatus Status = SprintStatus.Active);

public sealed record SprintActivationSnapshot(
    IReadOnlyList<IssueRecord> Issues, IReadOnlyList<SpecRecord> Specs, IReadOnlyList<string> TaskIds,
    long? ProposalAuditId = null, string? CommittedBy = null,
    IReadOnlyList<SprintDependencySnapshot>? Dependencies = null,
    IReadOnlyList<IssueRecord>? ExistingSprintMembers = null,
    SprintPlan? Plan = null);

public sealed record SprintDependencySnapshot(string BlockerId, string BlockedId, IssueStatus? BlockerStatus,
    string? BlockerMetadataJson = null, bool? BlockerSatisfied = null);

public interface ISprintStore
{
    Task<SprintRecord> CreateAsync(NewSprint spec, CancellationToken ct = default);
    Task<SprintRecord> CreateWithIssuesAsync(NewSprint spec, SprintActivationSnapshot snapshot, CancellationToken ct = default)
        => throw new NotSupportedException("Atomic sprint activation is required.");
    Task<IReadOnlyList<SprintRecord>> ListAsync(bool activeOnly, CancellationToken ct = default);
    Task<SprintRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<SprintRecord?> GetActiveAsync(CancellationToken ct = default);
    Task<SprintRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<SprintRecord> SetActiveAsync(string id, CancellationToken ct = default);
    Task<SprintRecord> ActivateWithIssuesAsync(string id, SprintActivationSnapshot snapshot,
        IReadOnlyList<string> expectedMembership, CancellationToken ct = default)
        => throw new NotSupportedException("Validated sprint activation is required.");
    Task AddIssueAsync(string sprintId, string issueId, CancellationToken ct = default);
    Task AddInactiveIssueAsync(string sprintId, string issueId, CancellationToken ct = default)
        => throw new NotSupportedException("Fenced inactive membership editing is required.");
    Task AddValidatedIssueAsync(string sprintId, SprintActivationSnapshot snapshot, CancellationToken ct = default)
        => throw new NotSupportedException("Validated sprint membership is required.");
    Task AddValidatedPrerequisiteAsync(string sprintId, SprintActivationSnapshot snapshot, CancellationToken ct = default)
        => throw new NotSupportedException("Validated prerequisite amendment is required.");
    Task RemoveIssueAsync(string sprintId, string issueId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetIssueIdsAsync(string sprintId, CancellationToken ct = default);
}

public sealed partial class SprintStore : ISprintStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public SprintStore(IssueStore issues) { _issues = issues; }

    private string T(string name) => _issues.Db.Dialect.Table(name);

    public Task<SprintRecord> CreateAsync(NewSprint spec, CancellationToken ct = default)
        => CreateCoreAsync(spec, null, ct);

    public Task<SprintRecord> CreateWithIssuesAsync(NewSprint spec, SprintActivationSnapshot snapshot, CancellationToken ct = default)
        => CreateCoreAsync(spec, snapshot, ct);

    private async Task<SprintRecord> CreateCoreAsync(NewSprint spec, SprintActivationSnapshot? snapshot, CancellationToken ct)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (snapshot?.Plan is { } approvedPlan) spec = spec with { Goal = approvedPlan.Objective, Name = approvedPlan.Objective };
        var id = $"sprint-{Guid.NewGuid().ToString("N")[..10]}";
        var now = DateTime.UtcNow;
        // Sequential sprint number (per store/project): names read
        // "Sprint N: <short goal>" so the board is scannable — bare
        // hash ids gave no ordering or goal signal.
        var number = await NextNumberAsync(conn, tx, ct);
        var baseName = spec.Name.Length > 60 ? spec.Name[..60].TrimEnd() : spec.Name;
        var name = System.Text.RegularExpressions.Regex.IsMatch(spec.Name, @"^Sprint \d+:", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? spec.Name
            : $"Sprint {number}: {baseName}";
        if (spec.Status == SprintStatus.Active)
        {
            await EnsureNoOtherActiveAsync(conn, tx, id, ct);
            if (snapshot?.Plan is not null) await EnsurePriorCompletionResolvedAsync(conn, tx, ct);
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {T("sprint")} (id, name, goal, start_date, end_date, status, created_at, updated_at)
                VALUES (@id, @name, @goal, @start, @end, @status, @now, @now)
                """;
            cmd.AddParam("@id", id);
            cmd.AddParam("@name", name);
            cmd.AddParam("@goal", spec.Goal);
            cmd.AddParam("@start", IssueStore.DateFormatTime(spec.StartDate));
            cmd.AddParam("@end", IssueStore.DateFormatTime(spec.EndDate));
            cmd.AddParam("@status", spec.Status.ToString());
            cmd.AddParam("@now", IssueStore.DateFormatTime(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (snapshot is not null)
        {
            await ValidateSnapshotAsync(conn, tx, snapshot, ct);
            await PersistPlanAsync(conn, tx, id, snapshot, ct);
            if (snapshot.Plan is null && snapshot.ProposalAuditId is { } auditId)
            {
                await using var audit = conn.CreateCommand();
                audit.Transaction = tx;
                audit.CommandText = $"UPDATE {T("sprint_proposal_audit")} SET committed_sprint_id=@sid, committed_by=@who WHERE id=@audit AND committed_sprint_id IS NULL";
                audit.AddParam("@sid", id);
                audit.AddParam("@who", snapshot.CommittedBy ?? "operator");
                audit.AddParam("@audit", auditId);
                if (await audit.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Proposal was already committed or no longer exists.");
            }
            foreach (var issue in snapshot.Issues)
            {
                await using var link = conn.CreateCommand();
                link.Transaction = tx;
                link.CommandText = $"INSERT INTO {T("sprint_issue")} (sprint_id, issue_id, added_at) VALUES (@sid, @iid, @now)";
                link.AddParam("@sid", id);
                link.AddParam("@iid", issue.Id);
                link.AddParam("@now", IssueStore.DateFormatTime(now));
                await link.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        await PublishStatusChangedAsync(id, fromStatus: null, spec.Status, now, ct);
        return new SprintRecord(id, name, spec.Goal, spec.StartDate, spec.EndDate, spec.Status, now, now);
    }

    /// <summary>Publish AFTER the mutation commits; the publisher
    /// swallows failures (a hint never breaks a DB mutation).</summary>
    private async Task PublishStatusChangedAsync(
        string sprintId, SprintStatus? fromStatus, SprintStatus toStatus, DateTime changedAt, CancellationToken ct)
    {
        if (fromStatus == toStatus) return;
        var now = new DateTimeOffset(changedAt, TimeSpan.Zero);
        await _issues.Events.PublishAsync(new Messaging.SprintStatusChanged
        {
            MessageId = Messaging.SprintStatusChanged.IdFor(_issues.ProjectId, sprintId, toStatus.ToString(), now),
            ProjectId = _issues.ProjectId,
            SprintId = sprintId,
            FromStatus = fromStatus?.ToString() ?? "(new)",
            ToStatus = toStatus.ToString(),
            ChangedAt = now,
        }, ct);
    }

    private async Task<int> NextNumberAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM {T("sprint")}";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return count + 1;
    }

    public async Task<IReadOnlyList<SprintRecord>> ListAsync(bool activeOnly, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = activeOnly
            ? $"SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM {T("sprint")} WHERE status='Active' ORDER BY start_date DESC"
            : $"SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM {T("sprint")} ORDER BY start_date DESC";
        var list = new List<SprintRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<SprintRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM {T("sprint")} WHERE id = @id";
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<SprintRecord?> GetActiveAsync(CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var d = _issues.Db.Dialect;
        cmd.CommandText = $"SELECT {d.Top(1)}id, name, goal, start_date, end_date, status, created_at, updated_at FROM {T("sprint")} WHERE status='Active'{d.Limit(1)}";
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<SprintRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct) ?? throw new InvalidOperationException($"Sprint {id} not found");
        // Field values arrive as JsonElement from the dashboard's
        // Dictionary<string,object?> deserialize — unwrap via ToString.
        static string? Str(object? v) => v is string s ? s : v?.ToString();
        var merged = existing with
        {
            Name = fields.TryGetValue("name", out var nm) && Str(nm) is { Length: > 0 } s1 ? s1 : existing.Name,
            Goal = fields.TryGetValue("goal", out var gl) && Str(gl) is { Length: > 0 } s2 ? s2 : existing.Goal,
            StartDate = fields.TryGetValue("startDate", out var sd) ? Convert.ToDateTime(Str(sd)) : existing.StartDate,
            EndDate = fields.TryGetValue("endDate", out var ed) ? Convert.ToDateTime(Str(ed)) : existing.EndDate,
            Status = fields.TryGetValue("status", out var st) ? Enum.Parse<SprintStatus>(st?.ToString() ?? "Active") : existing.Status,
        };
        var now = DateTime.UtcNow;
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct))
        {
            // Bind generic edits to the current row, not a pre-transaction active/inactive read.
            using var current = conn.CreateCommand(); current.Transaction = tx;
            current.CommandText = $"SELECT status,goal FROM {T("sprint")} WHERE id=@id";
            current.AddParam("@id", id);
            await using (var reader = await current.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct) || reader.GetString(0) != existing.Status.ToString() || reader.GetString(1) != existing.Goal)
                    throw new InvalidOperationException("Sprint changed; reload before editing.");
            }
            if ((merged.Status != existing.Status || merged.Goal != existing.Goal) && await HasCommittedAuditAsync(conn, tx, id, ct))
                throw new InvalidOperationException("Committed outcome status and goal are owned by completion evidence; use the explicit completion or stop action.");
            if (merged.Status == SprintStatus.Active) await EnsureNoOtherActiveAsync(conn, tx, id, ct);
            await using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
            ApplySprintUpdateCommand(cmd, merged, now); cmd.AddParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        await PublishStatusChangedAsync(id, existing.Status, merged.Status, now, ct);
        return merged with { UpdatedAt = now };
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (await HasCommittedAuditAsync(conn, tx, id, ct))
            throw new InvalidOperationException("Committed sprint history and delivery evidence cannot be deleted.");
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {T("sprint")} WHERE id = @id";
        cmd.AddParam("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public Task AddValidatedIssueAsync(string sprintId, SprintActivationSnapshot snapshot, CancellationToken ct = default)
        => AddValidatedIssueCoreAsync(sprintId, snapshot, false, ct);

    public Task AddValidatedPrerequisiteAsync(string sprintId, SprintActivationSnapshot snapshot, CancellationToken ct = default)
        => AddValidatedIssueCoreAsync(sprintId, snapshot, true, ct);

    private async Task AddValidatedIssueCoreAsync(string sprintId, SprintActivationSnapshot snapshot, bool requirePrerequisite, CancellationToken ct)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await using (var sprint = conn.CreateCommand())
        {
            sprint.Transaction = tx;
            sprint.CommandText = $"SELECT status FROM {T("sprint")} WHERE id=@id";
            sprint.AddParam("@id", sprintId);
            if (await sprint.ExecuteScalarAsync(ct) is not string status || status != SprintStatus.Active.ToString())
                throw new InvalidOperationException("Sprint is no longer active.");
        }
        await ValidateSnapshotAsync(conn, tx, snapshot, ct, sprintId);
        await PersistPlanAsync(conn, tx, sprintId, snapshot, ct);
        if (requirePrerequisite)
            await ValidateNecessityAsync(conn, tx, sprintId, snapshot, ct);
        foreach (var issue in snapshot.Issues)
        {
            await using var member = conn.CreateCommand();
            member.Transaction = tx;
            member.CommandText = $"INSERT INTO {T("sprint_issue")} (sprint_id,issue_id,added_at) SELECT @sid,@iid,@now WHERE NOT EXISTS (SELECT 1 FROM {T("sprint_issue")} WHERE sprint_id=@sid AND issue_id=@iid)";
            member.AddParam("@sid", sprintId);
            member.AddParam("@iid", issue.Id);
            member.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
            await member.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<SprintRecord> ActivateWithIssuesAsync(string id, SprintActivationSnapshot snapshot,
        IReadOnlyList<string> expectedMembership, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct) ?? throw new InvalidOperationException($"Sprint {id} not found");
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await EnsureNoOtherActiveAsync(conn, tx, id, ct);
        if (snapshot.Plan is not null) await EnsurePriorCompletionResolvedAsync(conn, tx, ct);
        await ValidateSnapshotAsync(conn, tx, snapshot, ct);
        await PersistPlanAsync(conn, tx, id, snapshot, ct);
        var currentMembers = new List<string>();
        await using (var members = conn.CreateCommand())
        {
            members.Transaction = tx;
            members.CommandText = $"SELECT issue_id FROM {T("sprint_issue")} WHERE sprint_id=@id";
            members.AddParam("@id", id);
            await using var row = await members.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) currentMembers.Add(row.GetString(0));
        }
        if (!currentMembers.Order(StringComparer.Ordinal).SequenceEqual(expectedMembership.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Sprint membership changed during activation.");
        var now = DateTime.UtcNow;
        foreach (var issue in snapshot.Issues)
        {
            using var member = conn.CreateCommand();
            member.Transaction = tx;
            member.CommandText = $"INSERT INTO {T("sprint_issue")} (sprint_id,issue_id,added_at) SELECT @sid,@iid,@now WHERE NOT EXISTS (SELECT 1 FROM {T("sprint_issue")} WHERE sprint_id=@sid AND issue_id=@iid)";
            member.AddParam("@sid", id);
            member.AddParam("@iid", issue.Id);
            member.AddParam("@now", IssueStore.DateFormatTime(now));
            await member.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE {T("sprint")} SET status='Active', updated_at=@now WHERE id=@id";
            cmd.AddParam("@id", id);
            cmd.AddParam("@now", IssueStore.DateFormatTime(now));
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException($"Sprint {id} no longer exists.");
        }
        await tx.CommitAsync(ct);
        await PublishStatusChangedAsync(id, existing.Status, SprintStatus.Active, now, ct);
        return existing with { Status = SprintStatus.Active, UpdatedAt = now };
    }

    public async Task<SprintRecord> SetActiveAsync(string id, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct);
        var now = DateTime.UtcNow;
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await EnsureNoOtherActiveAsync(conn, tx, id, ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""UPDATE {T("sprint")} SET status='Active', updated_at=@now WHERE id=@id""";
            cmd.AddParam("@now", IssueStore.DateFormatTime(now));
            cmd.AddParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        await PublishStatusChangedAsync(id, existing?.Status, SprintStatus.Active, now, ct);
        return (await GetAsync(id, ct))!;
    }

    public Task AddIssueAsync(string sprintId, string issueId, CancellationToken ct = default)
        => AddIssueCoreAsync(sprintId, issueId, false, ct);

    public Task AddInactiveIssueAsync(string sprintId, string issueId, CancellationToken ct = default)
        => AddIssueCoreAsync(sprintId, issueId, true, ct);

    private async Task AddIssueCoreAsync(string sprintId, string issueId, bool requireInactive, CancellationToken ct)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (requireInactive)
        {
            if (await HasCommittedAuditAsync(conn, tx, sprintId, ct))
                throw new InvalidOperationException("Committed sprint membership is immutable outside validated active amendments.");
            await using var check = conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = $"SELECT status FROM {T("sprint")} WHERE id=@sid";
            check.AddParam("@sid", sprintId);
            if (await check.ExecuteScalarAsync(ct) is not string status || status == "Active")
                throw new InvalidOperationException("Sprint became active; retry with validated membership.");
        }
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = _issues.Db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                IF NOT EXISTS (SELECT 1 FROM {T("sprint_issue")} WHERE sprint_id = @sid AND issue_id = @iid)
                INSERT INTO {T("sprint_issue")} (sprint_id, issue_id, added_at) VALUES (@sid, @iid, @now);
                """
            : """
                INSERT OR IGNORE INTO sprint_issue (sprint_id, issue_id, added_at)
                VALUES (@sid, @iid, @now)
                """;
        cmd.AddParam("@sid", sprintId);
        cmd.AddParam("@iid", issueId);
        cmd.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task RemoveIssueAsync(string sprintId, string issueId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (await HasCommittedAuditAsync(conn, tx, sprintId, ct))
            throw new InvalidOperationException("Required members and committed outcome history cannot be removed; use the explicit completion or stop action.");
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {T("sprint_issue")} WHERE sprint_id = @sid AND issue_id = @iid";
        cmd.AddParam("@sid", sprintId);
        cmd.AddParam("@iid", issueId);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetIssueIdsAsync(string sprintId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT issue_id FROM {T("sprint_issue")} WHERE sprint_id = @sid ORDER BY added_at";
        cmd.AddParam("@sid", sprintId);
        var ids = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetString(0));
        return ids;
    }

    private async Task EnsureNoOtherActiveAsync(DbConnection conn, DbTransaction tx, string keepId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM {T("sprint")} WHERE status='Active' AND id != @keep";
        cmd.AddParam("@keep", keepId);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) != 0)
            throw new InvalidOperationException("An active sprint already exists; complete it before activating another.");
    }

    private async Task ValidateSnapshotAsync(DbConnection conn, DbTransaction tx, SprintActivationSnapshot snapshot, CancellationToken ct, string? activeSprintId = null)
    {
        if (snapshot.TaskIds.Count == 0 || snapshot.TaskIds.Distinct(StringComparer.Ordinal).Count() != snapshot.TaskIds.Count)
            throw new InvalidOperationException("Sprint selection must contain distinct tasks.");
        await ValidateExistingMembersAsync(conn, tx, snapshot, activeSprintId, ct);
        foreach (var issue in snapshot.Issues.Concat(snapshot.ExistingSprintMembers ?? []))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT status, updated_at, metadata_json, COALESCE(parent_issue_id,''), title, COALESCE(description,''), type, priority FROM {T("issue")} WHERE id=@id";
            cmd.AddParam("@id", issue.Id);
            await using var row = await cmd.ExecuteReaderAsync(ct);
            // Compare exact text in managed code: SQL Server's default collation is
            // case-insensitive, including for JSON and task descriptions.
            if (!await row.ReadAsync(ct) || row.GetString(0) != issue.Status.ToString()
                || row.GetString(1) != IssueStore.DateFormatTime(issue.UpdatedAt)
                || row.GetString(2) != issue.MetadataJson || row.GetString(3) != (issue.ParentIssueId ?? "")
                || row.GetString(4) != issue.Title || row.GetString(5) != (issue.Description ?? "")
                || row.GetString(6) != issue.Type || Convert.ToInt32(row.GetValue(7)) != issue.Priority)
                throw new InvalidOperationException($"Sprint selection is stale: {issue.Id} changed.");
        }
        foreach (var id in snapshot.TaskIds)
        {
            if (!snapshot.Issues.Any(i => i.Id == id && i.Status == IssueStatus.Pending))
                throw new InvalidOperationException($"Sprint task {id} has no pending snapshot.");
        }
        await ValidateDependenciesAsync(conn, tx, snapshot, ct);
        foreach (var spec in snapshot.Specs)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT COUNT(*) FROM {T("spec")} WHERE id=@id AND project_id=@project AND status=@status AND current_version=@version";
            cmd.AddParam("@id", spec.Id);
            cmd.AddParam("@project", spec.ProjectId);
            cmd.AddParam("@status", spec.Status.ToString());
            cmd.AddParam("@version", spec.CurrentVersion);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) != 1)
                throw new InvalidOperationException($"Sprint specification is stale: {spec.Id} changed.");
        }
    }

    private void ApplySprintUpdateCommand(DbCommand cmd, SprintRecord s, DateTime now)
    {
        cmd.CommandText = $"""UPDATE {T("sprint")} SET name=@name, goal=@goal, start_date=@start, end_date=@end, status=@status, updated_at=@now WHERE id=@id""";
        cmd.AddParam("@name", s.Name);
        cmd.AddParam("@goal", s.Goal);
        cmd.AddParam("@start", IssueStore.DateFormatTime(s.StartDate));
        cmd.AddParam("@end", IssueStore.DateFormatTime(s.EndDate));
        cmd.AddParam("@status", s.Status.ToString());
        cmd.AddParam("@now", IssueStore.DateFormatTime(now));
    }

    private static SprintRecord Read(DbDataReader rd) => new(
        Id: rd.GetString(0),
        Name: rd.GetString(1),
        Goal: rd.GetString(2),
        StartDate: IssueStore.ParseTime(rd.GetString(3)),
        EndDate: IssueStore.ParseTime(rd.GetString(4)),
        Status: Enum.Parse<SprintStatus>(rd.GetString(5)),
        CreatedAt: IssueStore.ParseTime(rd.GetString(6)),
        UpdatedAt: IssueStore.ParseTime(rd.GetString(7)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}



