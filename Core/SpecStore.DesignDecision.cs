using System.Text.Json;
using Forge.Core.Initiatives;

namespace Forge.Core;

public sealed partial class SpecStore
{
    public async Task CommitDesignDecisionAsync(string id, SpecStatus status, long runId,
        IReadOnlyList<string> artifactIds, string hygieneJson, TimeSpan duration, CancellationToken ct = default)
    {
        if (status is not (SpecStatus.Designed or SpecStatus.Approved or SpecStatus.NeedsRevision))
            throw new InvalidOperationException("A designer must commit Designed, Approved or NeedsRevision.");
        await using var connection = await _issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await InitiativeDb.LockAsync(connection, tx, _issues.Db.Dialect, ct);
        using (var receipt = InitiativeDb.Command(connection, tx,
            $"SELECT status,new_spec_status FROM {T("designer_run")} WHERE id=@run AND spec_id=@spec",
            ("@run", runId), ("@spec", id)))
        {
            await using var reader = await receipt.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Designer run does not belong to this specification.");
            if (reader.GetString(0) == "succeeded")
            {
                if (reader.IsDBNull(1) || reader.GetString(1) != status.ToString())
                    throw new InvalidOperationException("A committed design decision cannot be replaced.");
                return; // Replay must never move a spec back after immediate grooming.
            }
            if (reader.GetString(0) != "started") throw new InvalidOperationException("Designer run is no longer active.");
        }
        SpecStatus from;
        using (var read = InitiativeDb.Command(connection, tx,
            $"SELECT status FROM {T("spec")} WHERE id=@id", ("@id", id)))
            from = Enum.Parse<SpecStatus>(await read.ExecuteScalarAsync(ct) as string
                ?? throw new InvalidOperationException("Specification not found."));
        SpecStatusTransitions.EnsureAllowed(from, status);
        var now = DateTimeOffset.UtcNow;
        using (var update = InitiativeDb.Command(connection, tx,
            $"UPDATE {T("spec")} SET status=@status,updated_at=@now WHERE id=@id AND status=@from AND NOT EXISTS (SELECT 1 FROM {T("initiative_ingestion")} r WHERE r.spec_id=@id AND r.refinement_state<>'Completed')",
            ("@status", status.ToString()), ("@now", IssueStore.DateFormatTime(now.UtcDateTime)),
            ("@id", id), ("@from", from.ToString())))
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Specification changed before design handoff.");
        using (var finish = InitiativeDb.Command(connection, tx,
            $"UPDATE {T("designer_run")} SET status='succeeded',new_spec_status=@status,design_artifact_ids=@artifacts,hygiene_report=@hygiene,error=NULL,duration_ms=@ms WHERE id=@run AND spec_id=@spec AND status='started'",
            ("@status", status.ToString()), ("@artifacts", JsonSerializer.Serialize(artifactIds)),
            ("@hygiene", hygieneJson), ("@ms", (long)duration.TotalMilliseconds), ("@run", runId), ("@spec", id)))
            if (await finish.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Designer run changed before handoff.");
        await tx.CommitAsync(ct);
        if (from != status)
            await _issues.Events.PublishAsync(new Messaging.SpecStatusChanged
            {
                MessageId = Messaging.SpecStatusChanged.IdFor(_issues.ProjectId, id, status.ToString(), now),
                ProjectId = _issues.ProjectId, SpecId = id, FromStatus = from.ToString(),
                ToStatus = status.ToString(), ChangedAt = now,
            }, ct);
    }
}
