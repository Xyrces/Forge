using System.Data;
using System.Text.Json;
using static Forge.Core.Initiatives.InitiativeDb;

namespace Forge.Core.Initiatives;

public sealed record HygieneRepairRequest(string SpecId, int SpecVersion, long DesignRunId);

public sealed partial class InitiativeIngestionStore
{
    private static string RepairKey(string specId, int version) => $"initiative/hygiene-repair/{specId}/{version}";

    public async Task<string?> GetRepairContextAsync(string specId, int version, CancellationToken ct = default)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        using var cmd = Command(c, null, $"SELECT body FROM {T("memory")} WHERE [key]=@key", ("@key", RepairKey(specId, version)));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public Task<bool> RepairHygieneAsync(string id, int revision, string hash, HygieneRepairRequest request, CancellationToken ct = default)
        => CheckOrRepairAsync(id, revision, hash, request, true, ct);

    public Task<bool> CanRepairHygieneAsync(string id, int revision, string hash, HygieneRepairRequest request, CancellationToken ct = default)
        => CheckOrRepairAsync(id, revision, hash, request, false, ct);

    private async Task<bool> CheckOrRepairAsync(string id, int revision, string hash, HygieneRepairRequest request, bool apply, CancellationToken ct)
    {
        await using var c = await issues.Db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAsync(c, tx, issues.Db.Dialect, ct);
        var receipt = await Get(c, tx, id, revision, ct);
        if (receipt is null || receipt.ProjectId != issues.ProjectId || receipt.Hash != hash
            || receipt.SpecId != request.SpecId || receipt.RefinementState != "Completed") return false;
        // Serializable reads fence concurrent status/version changes and downstream inserts.
        using var eligible = Command(c, tx, $"""
            SELECT s.updated_at FROM {T("spec")} s
            WHERE s.id=@spec AND s.project_id=@project AND s.current_version=@version AND s.status='ReadyForDesign'
            AND NOT EXISTS(SELECT 1 FROM {T("issue")} i WHERE i.parent_issue_id=s.id)
            AND NOT EXISTS(SELECT 1 FROM {T("spec")} child WHERE child.parent_spec_id=s.id)
            AND NOT EXISTS(SELECT 1 FROM {T("issue_groomer_run")} g WHERE g.spec_id=s.id)
            AND NOT EXISTS(SELECT 1 FROM {T("designer_run")} active WHERE active.spec_id=s.id AND active.status='started')
            """, ("@spec", request.SpecId), ("@project", issues.ProjectId), ("@version", request.SpecVersion));
        var updated = await eligible.ExecuteScalarAsync(ct) as string;
        if (updated is null) return false;
        using var run = Command(c, tx, $"""
            SELECT hygiene_report,error,ts FROM {T("designer_run")}
            WHERE id=@run AND spec_id=@spec AND status='hygiene_failed'
            AND NOT EXISTS(SELECT 1 FROM {T("designer_run")} newer WHERE newer.spec_id=@spec AND newer.id>@run)
            """, ("@run", request.DesignRunId), ("@spec", request.SpecId));
        string? report, error; string started;
        await using (var reader = await run.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return false;
            report = reader.IsDBNull(0) ? null : reader.GetString(0);
            error = reader.IsDBNull(1) ? null : reader.GetString(1);
            started = reader.GetString(2);
        }
        if (IssueStore.ParseTime(started) < IssueStore.ParseTime(updated) || string.IsNullOrWhiteSpace(report)) return false;
        if (!apply) return true;
        var context = JsonSerializer.Serialize(new { action = "operator-hygiene-repair", actor = "operator via private dashboard",
            proposalId = id, revision, hash, request.SpecId, request.SpecVersion, request.DesignRunId,
            requestedAt = Now(), hygieneReport = report, error });
        using (var memory = Command(c, tx, $"INSERT INTO {T("memory")}(ts,[key],body,ttl_days) VALUES(@now,@key,@body,NULL)",
            ("@now", Now()), ("@key", RepairKey(request.SpecId, request.SpecVersion)), ("@body", context)))
            await memory.ExecuteNonQueryAsync(ct);
        using (var spec = Command(c, tx, $"UPDATE {T("spec")} SET status='Draft',updated_at=@now WHERE id=@spec AND current_version=@version AND status='ReadyForDesign'",
            ("@now", Now()), ("@spec", request.SpecId), ("@version", request.SpecVersion)))
            if (await spec.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Specification changed during repair.");
        using (var reset = Command(c, tx, $"UPDATE {T("initiative_ingestion")} SET refinement_state='Pending',last_error=@context,updated_at=@now WHERE proposal_id=@id AND revision=@revision AND refinement_state='Completed'",
            ("@context", context), ("@now", Now()), ("@id", id), ("@revision", revision)))
            if (await reset.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Refinement changed during repair.");
        await tx.CommitAsync(ct);
        return true;
    }
}
