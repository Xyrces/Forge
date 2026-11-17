using System.Data.Common;
using Forge.Core.Db;
using static Forge.Core.Initiatives.InitiativeDb;

namespace Forge.Core.Initiatives;

/// <summary>Project-local atomic ingestion and durable refinement bookkeeping; no model calls.</summary>
public sealed class InitiativeIngestionStore(IssueStore issues)
{
    private string T(string name) => issues.Db.Dialect.Table(name);
    private string Query => $"SELECT proposal_id,revision,project_id,content_hash,epic_id,spec_id,refinement_state,attempts,last_error,updated_at FROM {T("initiative_ingestion")}";
    private static InitiativeIngestion Read(DbDataReader r) => new(r.GetString(0), Convert.ToInt32(r.GetValue(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), Convert.ToInt32(r.GetValue(7)), r.IsDBNull(8) ? null : r.GetString(8), IssueStore.ParseTime(r.GetString(9)));

    private async Task<InitiativeIngestion?> Get(DbConnection c, DbTransaction? tx, string id, int revision, CancellationToken ct)
    {
        using var cmd = Command(c, tx, Query + " WHERE proposal_id=@id AND revision=@revision",
            ("@id", id),
            ("@revision", revision));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<InitiativeIngestion?> GetAsync(string id, int revision, CancellationToken ct = default)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        return await Get(c, null, id, revision, ct);
    }

    public async Task<InitiativeIngestion?> GetBySpecAsync(string specId, CancellationToken ct = default)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        using var cmd = Command(c, null, Query + " WHERE spec_id=@spec", ("@spec", specId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<InitiativeIngestion>> ListPendingRefinementAsync(CancellationToken ct = default, int limit = 50, string? afterId = null)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        using var cmd = Command(c, null, Query.Replace("SELECT ", $"SELECT {issues.Db.Dialect.Top(Math.Clamp(limit, 1, 100))}", StringComparison.Ordinal) + " WHERE refinement_state='Pending'" + (afterId is null ? "" : " AND proposal_id>@after") + $" ORDER BY proposal_id{issues.Db.Dialect.Limit(Math.Clamp(limit, 1, 100))}",
            ("@after", afterId));
        var result = new List<InitiativeIngestion>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(Read(r));
        return result;
    }

    public async Task<InitiativeIngestion> IngestAsync(InitiativeProposal approved, CancellationToken ct = default)
    {
        if (approved.ProjectId != issues.ProjectId)
            throw new InvalidOperationException("Proposal belongs to another project.");
        if (approved.ApprovedAt is null || string.IsNullOrWhiteSpace(approved.ApprovedBy) || approved.SessionId is null
            || approved.Hash != Hash(approved.ProjectId, approved.Title, approved.Body, approved.Priority))
            throw new InvalidOperationException("An intact explicitly approved proposal is required.");
        Payload(approved.Title, approved.Body, approved.Priority);
        await using var c = await issues.Db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await LockAsync(c, tx, issues.Db.Dialect, ct);
        var existing = await Get(c, tx, approved.Id, approved.Revision, ct);
        if (existing is not null)
        {
            if (existing.ProjectId != approved.ProjectId || existing.Hash != approved.Hash)
                throw new InvalidOperationException("Conflicting approved content.");
            await tx.CommitAsync(ct);
            return existing;
        }
        var epic = await issues.CreateInTransactionAsync(c, tx, new NewIssue("epic", approved.Title, approved.Body, approved.Priority, "intake",
            Metadata: new Dictionary<string, object> { { "proposalId", approved.Id }, { "proposalRevision", approved.Revision }, { "proposalHash", approved.Hash } }), ct);
        var spec = await new SpecStore(issues).CreateInTransactionAsync(c, tx, new NewSpec(approved.ProjectId, approved.Title, approved.Body, Author: "intake", ParentIssueId: epic.Id), ct);
        using (var cmd = Command(c, tx, $"INSERT INTO {T("initiative_ingestion")}(proposal_id,revision,project_id,content_hash,epic_id,spec_id,refinement_state,attempts,updated_at) VALUES(@id,@revision,@project,@hash,@epic,@spec,'Pending',0,@now)",
            ("@id", approved.Id),
            ("@revision", approved.Revision),
            ("@project", approved.ProjectId),
            ("@hash", approved.Hash),
            ("@epic", epic.Id),
            ("@spec", spec.Id),
            ("@now", Now())))
            await cmd.ExecuteNonQueryAsync(ct);
        var result = (await Get(c, tx, approved.Id, approved.Revision, ct))!;
        await tx.CommitAsync(ct);
        await issues.PublishCreatedAsync(epic, ct);
        return result;
    }

    public async Task<bool> TryBeginRefinementAsync(string id, int revision, CancellationToken ct = default)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await LockAsync(c, tx, issues.Db.Dialect, ct);
        using var cmd = Command(c, tx, $"UPDATE {T("initiative_ingestion")} SET refinement_state='Running',attempts=attempts+1,last_error=NULL,updated_at=@now WHERE proposal_id=@id AND revision=@revision AND refinement_state='Pending'",
            ("@now", Now()),
            ("@id", id),
            ("@revision", revision));
        var changed = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<bool> CompleteRefinementAsync(string id, int revision, CancellationToken ct = default)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await LockAsync(c, tx, issues.Db.Dialect, ct);
        var receipt = await Get(c, tx, id, revision, ct) ?? throw new InvalidOperationException("Ingestion receipt missing.");
        if (receipt.RefinementState == "Completed")
        {
            await tx.CommitAsync(ct);
            return true;
        }
        if (receipt.RefinementState != "Running") return false;
        string? prior;
        using (var read = Command(c, tx, $"SELECT status FROM {T("spec")} WHERE id=@id AND project_id=@project",
            ("@id", receipt.SpecId),
            ("@project", issues.ProjectId)))
            prior = (await read.ExecuteScalarAsync(ct)) as string;
        if (prior is not ("Draft" or "ReadyForDesign"))
            throw new InvalidOperationException("Spec is not eligible for refinement completion.");
        using (var cmd = Command(c, tx, $"UPDATE {T("spec")} SET status='ReadyForDesign',updated_at=@now WHERE id=@id AND status='Draft'",
            ("@now", Now()),
            ("@id", receipt.SpecId)))
            await cmd.ExecuteNonQueryAsync(ct);
        using (var cmd = Command(c, tx, $"UPDATE {T("initiative_ingestion")} SET refinement_state='Completed',last_error=NULL,updated_at=@now WHERE proposal_id=@id AND revision=@revision",
            ("@now", Now()),
            ("@id", id),
            ("@revision", revision)))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        if (prior == "Draft")
            await issues.Events.PublishAsync(new Messaging.SpecStatusChanged { MessageId = $"initiative-refined:{id}:{revision}", ProjectId = issues.ProjectId, SpecId = receipt.SpecId, FromStatus = "Draft", ToStatus = "ReadyForDesign", ChangedAt = DateTimeOffset.UtcNow }, ct);
        return true;
    }

    public async Task RecordRefinementFailureAsync(string id, int revision, string error, CancellationToken ct = default)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await LockAsync(c, tx, issues.Db.Dialect, ct);
        using var cmd = Command(c, tx, $"UPDATE {T("initiative_ingestion")} SET refinement_state='Failed',last_error=@error,updated_at=@now WHERE proposal_id=@id AND revision=@revision AND refinement_state='Running'",
            ("@error", error),
            ("@now", Now()),
            ("@id", id),
            ("@revision", revision));
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> RetryRefinementAsync(string id, int revision, CancellationToken ct = default)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await LockAsync(c, tx, issues.Db.Dialect, ct);
        using var cmd = Command(c, tx, $"UPDATE {T("initiative_ingestion")} SET refinement_state='Pending',updated_at=@now WHERE proposal_id=@id AND revision=@revision AND refinement_state='Failed'",
            ("@now", Now()),
            ("@id", id),
            ("@revision", revision));
        var changed = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }
    /// <summary>Exclusive-controller startup only. Interrupted paid work remains failed/uncertain until explicit retry.</summary>

    public async Task<int> RecoverRunningAsync(CancellationToken ct = default)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await LockAsync(c, tx, issues.Db.Dialect, ct);
        using var cmd = Command(c, tx, $"UPDATE {T("initiative_ingestion")} SET refinement_state='Failed',last_error='Refinement interrupted; outcome uncertain. Explicit review and retry required.',updated_at=@now WHERE refinement_state='Running'",
            ("@now", Now()));
        var changed = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return changed;
    }
}

