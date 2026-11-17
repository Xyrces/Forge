using System.Data;
using System.Text.Json;

namespace Forge.Core.Control;

public sealed record BudgetCoveredRequest(string RequestId, decimal OriginalUsd);
public sealed record OperatorBudgetReconciliation(string DatasetId, string ReconciliationId, decimal ReportedUsd,
    string Operator, string EvidenceSha256, IReadOnlyList<BudgetCoveredRequest> Requests);
public sealed record BudgetReconciliationResult(bool IsNew, decimal OriginalUsd, decimal OperatorReportedUsd);

public sealed partial class DurableBudgetStore
{
    /// <summary>Explicit privileged operator action, never invoked automatically from settlement or a model tool.
    /// Replaces only the covered completed liability in admission arithmetic; original audit rows remain immutable.
    /// The source is operator-reported, not provider-verified billing.</summary>
    public async Task<BudgetReconciliationResult> ReconcileOperatorReportedAsync(OperatorBudgetReconciliation request,
        CancellationToken ct = default)
    {
        ControlDb.ValidateId(request.DatasetId, nameof(request.DatasetId));
        ControlDb.ValidateId(request.ReconciliationId, nameof(request.ReconciliationId));
        ControlDb.ValidateId(request.Operator, nameof(request.Operator));
        if (request.EvidenceSha256.Length != 64 || request.EvidenceSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A SHA256 evidence manifest hash is required.");
        if (request.Requests.Count is < 1 or > 1000 || request.Requests.Select(x => x.RequestId).Distinct(StringComparer.Ordinal).Count() != request.Requests.Count)
            throw new ArgumentException("Reconciliation requires a nonempty unique request manifest (maximum 1000).");
        var reported = ControlDb.Money(request.ReportedUsd, allowZero: true);
        var covered = request.Requests.OrderBy(x => x.RequestId, StringComparer.Ordinal).Select(x =>
        {
            ControlDb.ValidateId(x.RequestId, nameof(x.RequestId));
            return new { x.RequestId, OriginalMicrousd = ControlDb.Money(x.OriginalUsd, allowZero: true) };
        }).ToArray();
        var original = covered.Sum(x => x.OriginalMicrousd);
        var manifest = JsonSerializer.Serialize(new { request.DatasetId, request.ReconciliationId, ReportedMicrousd = reported,
            Source = "operator-reported", request.Operator, EvidenceSha256 = request.EvidenceSha256.ToLowerInvariant(), Requests = covered });
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await ControlDb.LockBudgetAsync(_db, connection, transaction, request.DatasetId, ct))
            throw new InvalidOperationException("Dataset is not initialized.");
        using (var existing = ControlDb.Command(connection, transaction,
            $"SELECT manifest_json FROM {_db.Dialect.Table("budget_reconciliation")} WHERE reconciliation_id=@id", ("@id", request.ReconciliationId)))
        {
            var prior = await existing.ExecuteScalarAsync(ct);
            if (prior is string priorManifest)
            {
                if (priorManifest != manifest) throw new InvalidOperationException("Reconciliation identity already binds different immutable evidence.");
                return new(false, ControlDb.Usd(original), ControlDb.Usd(reported));
            }
        }
        foreach (var row in covered)
        {
            var existing = await ReadReservationAsync(connection, transaction, row.RequestId, ct);
            if (existing is null || existing.Request.DatasetId != request.DatasetId || existing.Status != "completed" ||
                existing.ActualUsd != ControlDb.Usd(row.OriginalMicrousd))
                throw new InvalidOperationException("Covered request is missing, unfinished, cross-dataset or has a different immutable settlement.");
            using var overlap = ControlDb.Command(connection, transaction,
                $"SELECT COUNT(*) FROM {_db.Dialect.Table("budget_reconciliation_request")} WHERE request_id=@id", ("@id", row.RequestId));
            if (Convert.ToInt64(await overlap.ExecuteScalarAsync(ct)) != 0)
                throw new InvalidOperationException("A request cannot be covered by more than one reconciliation.");
        }
        using (var header = ControlDb.Command(connection, transaction, $"""
            INSERT INTO {_db.Dialect.Table("budget_reconciliation")}
                (reconciliation_id,dataset_id,original_microusd,reported_microusd,source,operator_name,evidence_sha256,manifest_json,created_at)
            VALUES(@id,@dataset,@original,@reported,'operator-reported',@operator,@evidence,@manifest,{ControlDb.Now(_db)})
            """, ("@id", request.ReconciliationId), ("@dataset", request.DatasetId), ("@original", original), ("@reported", reported),
            ("@operator", request.Operator), ("@evidence", request.EvidenceSha256.ToLowerInvariant()), ("@manifest", manifest)))
            await header.ExecuteNonQueryAsync(ct);
        foreach (var row in covered)
        {
            using var entry = ControlDb.Command(connection, transaction, $"""
                INSERT INTO {_db.Dialect.Table("budget_reconciliation_request")}
                    (request_id,reconciliation_id,dataset_id,original_microusd) VALUES(@request,@id,@dataset,@original)
                """, ("@request", row.RequestId), ("@id", request.ReconciliationId), ("@dataset", request.DatasetId), ("@original", row.OriginalMicrousd));
            await entry.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(true, ControlDb.Usd(original), ControlDb.Usd(reported));
    }
}
