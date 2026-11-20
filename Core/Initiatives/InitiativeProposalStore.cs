using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;
using static Forge.Core.Initiatives.InitiativeDb;

namespace Forge.Core.Initiatives;

public sealed partial class InitiativeProposalStore(IssueStore issues)
{
    private string T(string name) => issues.Db.Dialect.Table(name);
    private const string Columns = "p.id,p.project_id,r.revision,r.content_hash,r.title,r.body,r.priority,p.origin,p.source_key,p.source_json,p.session_id,p.created_at,r.approved_at,r.approved_by,r.epic_id,r.spec_id";
    private string Query => $"SELECT {Columns} FROM {T("initiative_proposal")} p JOIN {T("initiative_revision")} r ON r.proposal_id=p.id AND r.revision=p.current_revision";
    private static InitiativeProposal Read(DbDataReader r) => new(r.GetString(0), r.GetString(1), Convert.ToInt32(r.GetValue(2)), r.GetString(3), r.GetString(4), r.GetString(5), Convert.ToInt32(r.GetValue(6)), r.GetString(7), r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), IssueStore.ParseTime(r.GetString(11)), r.IsDBNull(12) ? null : IssueStore.ParseTime(r.GetString(12)), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15));

    private async Task<InitiativeProposal?> Get(DbConnection c, DbTransaction? tx, string projectId, string id, CancellationToken ct)
    {
        using var cmd = Command(c, tx, Query + " WHERE p.project_id=@project AND p.id=@id",
            ("@project", projectId),
            ("@id", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<InitiativeProposal?> GetAsync(string projectId, string id, CancellationToken ct = default)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        return await Get(c, null, projectId, id, ct);
    }

    public Task<IReadOnlyList<InitiativeProposalSummary>> ListAsync(string projectId, CancellationToken ct = default,
        int limit = 50, string? afterId = null, string? sessionId = null)
        => List(projectId, false, limit, afterId, sessionId, ct);


    public Task<IReadOnlyList<InitiativeProposalSummary>> ListApprovedAsync(string projectId, CancellationToken ct = default,
        int limit = 50, string? afterId = null)
        => List(projectId, true, limit, afterId, null, ct);


    private async Task<IReadOnlyList<InitiativeProposalSummary>> List(string projectId, bool approved,
        int limit, string? afterId, string? sessionId, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        await using var connection = await issues.Db.OpenAsync(ct);
        // Full proposal body/provenance loads only through GetAsync after selection.
        var sql = $"SELECT {issues.Db.Dialect.Top(limit)}p.id,p.project_id,r.revision,r.content_hash,r.title,r.priority,p.origin,p.source_key,p.session_id,p.created_at,r.approved_at,r.approved_by,r.epic_id,r.spec_id FROM {T("initiative_proposal")} p JOIN {T("initiative_revision")} r ON r.proposal_id=p.id AND r.revision=p.current_revision WHERE p.project_id=@project"
            + (approved ? " AND r.approved_at IS NOT NULL AND r.epic_id IS NULL" : "")
            + (afterId is null ? "" : " AND p.id>@after")
            + (sessionId is null ? "" : " AND p.session_id=@session")
            + $" ORDER BY p.id{issues.Db.Dialect.Limit(limit)}";
        using var command = Command(connection, null, sql,
            ("@project", projectId),
            ("@after", afterId),
            ("@session", sessionId));
        var result = new List<InitiativeProposalSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), Convert.ToInt32(reader.GetValue(2)),
                reader.GetString(3), reader.GetString(4), Convert.ToInt32(reader.GetValue(5)), reader.GetString(6),
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), IssueStore.ParseTime(reader.GetString(9)),
                reader.IsDBNull(10) ? null : IssueStore.ParseTime(reader.GetString(10)), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        return result;
    }


    /// <summary>Bounded recovery lookup for source drafts still awaiting disposition acknowledgement.
    /// Continue with the final proposal ID when a full page is returned.</summary>
    public async Task<IReadOnlyList<InitiativeSourceBinding>> FindBySourceDraftIdsAsync(string projectId,
        IReadOnlyList<long> sourceDraftIds, CancellationToken ct = default, string? afterId = null)
    {
        if (sourceDraftIds.Count is < 1 or > 100 || sourceDraftIds.Any(id => id <= 0))
            throw new ArgumentException("A source lookup requires 1 to 100 positive draft IDs.");
        await using var connection = await issues.Db.OpenAsync(ct);
        var values = sourceDraftIds.Distinct().Select((id, index) => ($"@draft{index}", (object?)id)).ToArray();
        var parameters = string.Join(',', values.Select(v => v.Item1));
        var jsonMatch = issues.Db.Provider == ForgeDbProvider.SqlServer
            ? $"EXISTS(SELECT 1 FROM OPENJSON(p.source_json,'$.sourceDraftIds') j WHERE TRY_CONVERT(bigint,j.value) IN ({parameters}))"
            : $"EXISTS(SELECT 1 FROM json_each(p.source_json,'$.sourceDraftIds') j WHERE CAST(j.value AS INTEGER) IN ({parameters}))";
        using var command = Command(connection, null,
            $"SELECT {issues.Db.Dialect.Top(100)}p.id,p.source_json FROM {T("initiative_proposal")} p WHERE p.project_id=@project AND p.origin='forge' AND {jsonMatch}"
            + (afterId is null ? "" : " AND p.id>@after") + $" ORDER BY p.id{issues.Db.Dialect.Limit(100)}",
            values.Concat(new[] { ("@project", (object?)projectId), ("@after", (object?)afterId) }).ToArray());
        var bindings = new List<InitiativeSourceBinding>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            bindings.Add(new(reader.GetString(0), reader.GetString(1)));
        return bindings;
    }

    private async Task<TResult> Write<TResult>(Func<DbConnection, DbTransaction, Task<TResult>> action, CancellationToken ct)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await LockAsync(c, tx, issues.Db.Dialect, ct);
        var result = await action(c, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task InsertRevision(DbConnection c, DbTransaction tx, string id, int revision, string project, string title, string body, int priority, CancellationToken ct)
    {
        using var cmd = Command(c, tx, $"INSERT INTO {T("initiative_revision")}(proposal_id,revision,content_hash,title,body,priority,created_at) VALUES(@id,@revision,@hash,@title,@body,@priority,@now)",
            ("@id", id),
            ("@revision", revision),
            ("@hash", Hash(project, title, body, priority)),
            ("@title", title),
            ("@body", body),
            ("@priority", priority),
            ("@now", Now()));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ValidateSession(DbConnection c, DbTransaction tx, string projectId, string sessionId, CancellationToken ct)
    {
        using var cmd = Command(c, tx, $"SELECT project_id FROM {T("intake_session")} WHERE id=@id",
            ("@id", sessionId));
        if ((await cmd.ExecuteScalarAsync(ct)) as string != projectId)
            throw new InvalidOperationException("Intake session belongs to another project or is missing.");
    }

    public Task<InitiativeProposal> CreateAsync(NewInitiativeProposal request, CancellationToken ct = default)
    {
        Text(request.ProjectId, "project");
        Text(request.SourceKey, "source key");
        Payload(request.Title, request.Body, request.Priority);
        if (request.Origin is not ("user" or "forge"))
            throw new ArgumentException("Invalid proposal origin.");
        using (var source = JsonDocument.Parse(request.SourceJson)) { }
        return Write(async (c, tx) =>
        {
            using (var existing = Command(c, tx, Query + " WHERE p.project_id=@project AND p.source_key=@source",
            ("@project", request.ProjectId),
            ("@source", request.SourceKey)))
            {
                await using var reader = await existing.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var original = Read(reader);
                    if (original.Origin != request.Origin || original.SourceJson != request.SourceJson
                        || (request.SessionId is not null && original.SessionId != request.SessionId))
                        throw new InvalidOperationException("Source identity is already bound to different provenance or session.");
                    return original;
                }
            }
            if (request.SessionId is not null)
                await ValidateSession(c, tx, request.ProjectId, request.SessionId, ct);
            var id = "proposal-" + Guid.NewGuid().ToString("N");
            using (var cmd = Command(c, tx, $"INSERT INTO {T("initiative_proposal")}(id,project_id,source_key,origin,source_json,session_id,current_revision,created_at) VALUES(@id,@project,@source,@origin,@json,@session,1,@now)",
            ("@id", id),
            ("@project", request.ProjectId),
            ("@source", request.SourceKey),
            ("@origin", request.Origin),
            ("@json", request.SourceJson),
            ("@session", request.SessionId),
            ("@now", Now())))
                await cmd.ExecuteNonQueryAsync(ct);
            await InsertRevision(c, tx, id, 1, request.ProjectId, request.Title, request.Body, request.Priority, ct);
            return (await Get(c, tx, request.ProjectId, id, ct))!;
        }, ct);
    }

    public Task<InitiativeProposal> ReviseAsync(string projectId, string id, int expectedRevision, string title, string body, int priority, CancellationToken ct = default)
    {
        Payload(title, body, priority);
        return Write(async (c, tx) =>
        {
            var current = await Get(c, tx, projectId, id, ct) ?? throw new InvalidOperationException("Proposal missing.");
            if (current.Revision != expectedRevision || current.ApprovedAt is not null)
                throw new InvalidOperationException("Proposal changed or already approved.");
            if (current.Hash == Hash(projectId, title, body, priority)) return current;
            await InsertRevision(c, tx, id, expectedRevision + 1, projectId, title, body, priority, ct);
            using var cmd = Command(c, tx, $"UPDATE {T("initiative_proposal")} SET current_revision=@revision WHERE id=@id",
            ("@revision", expectedRevision + 1),
            ("@id", id));
            await cmd.ExecuteNonQueryAsync(ct);
            return (await Get(c, tx, projectId, id, ct))!;
        }, ct);
    }

    public Task<InitiativeProposal> ApproveAsync(string projectId, string id, int revision, string hash, string approvedBy, CancellationToken ct = default)
    {
        Text(approvedBy, "operator attribution");
        return Write(async (c, tx) =>
        {
            var current = await Get(c, tx, projectId, id, ct) ?? throw new InvalidOperationException("Proposal missing.");
            if (current.Revision != revision || current.Hash != hash)
                throw new InvalidOperationException("Proposal revision changed; review current content.");
            if (current.ApprovedAt is not null) return current;
            if (current.SessionId is null)
                throw new InvalidOperationException("Expand proposal in Intake before approval.");
            using var cmd = Command(c, tx, $"UPDATE {T("initiative_revision")} SET approved_at=@now,approved_by=@operator WHERE proposal_id=@id AND revision=@revision AND approved_at IS NULL",
            ("@now", Now()),
            ("@operator", approvedBy),
            ("@id", id),
            ("@revision", revision));
            await cmd.ExecuteNonQueryAsync(ct);
            return (await Get(c, tx, projectId, id, ct))!;
        }, ct);
    }

    public Task<string> LinkSessionAsync(string projectId, string id, string sessionId, CancellationToken ct = default) => Write(async (c, tx) =>
    {
        var current = await Get(c, tx, projectId, id, ct) ?? throw new InvalidOperationException("Proposal missing.");
        await ValidateSession(c, tx, projectId, sessionId, ct);
        if (current.SessionId is not null && current.SessionId != sessionId)
            throw new InvalidOperationException("Proposal already linked to another Intake session.");
        using var cmd = Command(c, tx, $"UPDATE {T("initiative_proposal")} SET session_id=@session WHERE id=@id",
            ("@session", sessionId),
            ("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
        return sessionId;
    }, ct);

    public Task<string> ExpandInIntakeAsync(string projectId, string id, CancellationToken ct = default) => Write(async (c, tx) =>
    {
        var current = await Get(c, tx, projectId, id, ct) ?? throw new InvalidOperationException("Proposal missing.");
        if (current.SessionId is not null)
        {
            await ValidateSession(c, tx, projectId, current.SessionId, ct);
            return current.SessionId;
        }
        var session = "intake-" + Guid.NewGuid().ToString("N");
        using (var cmd = Command(c, tx, $"INSERT INTO {T("intake_session")}(id,project_id,title,created_at,updated_at) VALUES(@session,@project,@title,@now,@now)",
            ("@session", session),
            ("@project", projectId),
            ("@title", current.Title),
            ("@now", Now())))
            await cmd.ExecuteNonQueryAsync(ct);
        using (var cmd = Command(c, tx, $"INSERT INTO {T("intake_message")}(session_id,role,content,ts) VALUES(@session,'System',@content,@now)",
            ("@session", session),
            ("@content", $"Proposal {id}, revision {current.Revision}: {current.Title}\n\n{current.Body}\n\nSource: {current.SourceJson}\nThis is an unapproved proposal for discussion, not authorization to ingest work."),
            ("@now", Now())))
            await cmd.ExecuteNonQueryAsync(ct);
        using (var cmd = Command(c, tx, $"UPDATE {T("initiative_proposal")} SET session_id=@session WHERE id=@id",
            ("@session", session),
            ("@id", id)))
            await cmd.ExecuteNonQueryAsync(ct);
        return session;
    }, ct);

    public async Task MarkIngestedAsync(string projectId, string id, int revision, string epicId, string specId, CancellationToken ct = default)
    {
        Text(epicId, "epic ID");
        Text(specId, "spec ID");
        await Write(async (c, tx) =>
        {
            var current = await Get(c, tx, projectId, id, ct) ?? throw new InvalidOperationException("Proposal missing.");
            if (current.Revision != revision || current.ApprovedAt is null)
                throw new InvalidOperationException("Approval required.");
            if (current.EpicId is not null && (current.EpicId != epicId || current.SpecId != specId))
                throw new InvalidOperationException("Conflicting ingestion receipt.");
            using var cmd = Command(c, tx, $"UPDATE {T("initiative_revision")} SET epic_id=@epic,spec_id=@spec WHERE proposal_id=@id AND revision=@revision",
            ("@epic", epicId),
            ("@spec", specId),
            ("@id", id),
            ("@revision", revision));
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }
}
