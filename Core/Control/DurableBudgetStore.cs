using System.Data;
using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core.Control;

public sealed record BudgetReservationRequest(string DatasetId, string RequestId, string ProjectId,
    string RunId, string Provider, string Model, decimal ReservationUsd);
public sealed record BudgetReservation(BudgetReservationRequest Request, string Status, decimal? ActualUsd);
public sealed record BudgetReservationResult(bool Admitted, bool IsNew, BudgetReservation? Reservation, string? Reason);
public sealed record BudgetSnapshot(decimal LimitUsd, decimal SpentUsd, decimal OutstandingUsd);

public interface IDurableBudgetStore
{
    Task<BudgetReservationResult> ReserveAsync(BudgetReservationRequest request, CancellationToken ct = default);
    Task<bool> CompleteAsync(string datasetId, string requestId, decimal? actualCostUsd, CancellationToken ct = default);
    Task<BudgetSnapshot?> ReadAsync(string datasetId, CancellationToken ct = default);
}

/// <summary>Persist liability before paid work. Unknown outcomes never release their reservation automatically.</summary>
public sealed class DurableBudgetStore : IDurableBudgetStore
{
    private readonly IDbConnectionFactory _db;
    public DurableBudgetStore(IDbConnectionFactory db) { ControlDb.ValidateFactory(db); _db = db; }

    public async Task<BudgetReservationResult> ReserveAsync(BudgetReservationRequest request, CancellationToken ct = default)
    {
        foreach (var (value, name) in new[]
        {
            (request.DatasetId, nameof(request.DatasetId)), (request.RequestId, nameof(request.RequestId)),
            (request.ProjectId, nameof(request.ProjectId)), (request.RunId, nameof(request.RunId)),
            (request.Provider, nameof(request.Provider)), (request.Model, nameof(request.Model)),
        }) ControlDb.ValidateId(value, name);
        var bound = ControlDb.Money(request.ReservationUsd);
        var normalized = request with { ReservationUsd = ControlDb.Usd(bound) };
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await ControlDb.LockBudgetAsync(_db, connection, transaction, request.DatasetId, ct))
            return new(false, false, null, "dataset_not_initialized");

        var existing = await ReadReservationAsync(connection, transaction, request.RequestId, ct);
        if (existing is not null)
        {
            if (existing.Request != normalized)
                throw new InvalidOperationException("Request identity is already bound to different immutable reservation inputs.");
            return new(true, false, existing, "already_reserved_do_not_resubmit");
        }
        var budget = (await ReadAsync(connection, transaction, request.DatasetId, ct))!;
        if (budget.SpentUsd + budget.OutstandingUsd + normalized.ReservationUsd > budget.LimitUsd)
            return new(false, false, null, "budget_exhausted");
        using var insert = ControlDb.Command(connection, transaction, $"""
            INSERT INTO {_db.Dialect.Table("budget_reservation")}
                (request_id,dataset_id,project_id,run_id,provider,model,reservation_microusd,status,created_at,updated_at)
            VALUES(@request,@dataset,@project,@run,@provider,@model,@bound,'reserved',{ControlDb.Now(_db)},{ControlDb.Now(_db)})
            """, ("@request", request.RequestId), ("@dataset", request.DatasetId), ("@project", request.ProjectId),
            ("@run", request.RunId), ("@provider", request.Provider), ("@model", request.Model), ("@bound", bound));
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(true, true, new(normalized, "reserved", null), null);
    }

    public async Task<bool> CompleteAsync(string datasetId, string requestId, decimal? actualCostUsd, CancellationToken ct = default)
    {
        var actual = actualCostUsd.HasValue ? ControlDb.Money(actualCostUsd.Value, allowZero: true) : (long?)null;
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await ControlDb.LockBudgetAsync(_db, connection, transaction, datasetId, ct)) return false;
        var existing = await ReadReservationAsync(connection, transaction, requestId, ct);
        if (existing is null || existing.Request.DatasetId != datasetId) return false;
        if (existing.Status == "completed")
        {
            if (!actual.HasValue) return true; // A later cancellation cannot erase known spend.
            if (existing.ActualUsd != ControlDb.Usd(actual.Value))
                throw new InvalidOperationException("A settled request cannot be repriced by replay.");
            return true;
        }
        using var update = ControlDb.Command(connection, transaction,
            $"UPDATE {_db.Dialect.Table("budget_reservation")} SET actual_microusd=@actual,status=@status,updated_at={ControlDb.Now(_db)} WHERE request_id=@request AND dataset_id=@dataset",
            ("@actual", actual), ("@status", actual.HasValue ? "completed" : "uncertain"),
            ("@request", requestId), ("@dataset", datasetId));
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<BudgetSnapshot?> ReadAsync(string datasetId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        return await ReadAsync(connection, null, datasetId, ct);
    }

    private async Task<BudgetSnapshot?> ReadAsync(DbConnection connection, DbTransaction? transaction,
        string datasetId, CancellationToken ct)
    {
        using var command = ControlDb.Command(connection, transaction, $"""
            SELECT d.budget_limit_microusd,
                COALESCE((SELECT SUM(r.actual_microusd) FROM {_db.Dialect.Table("budget_reservation")} r WHERE r.dataset_id=d.dataset_id AND r.status='completed'),0),
                COALESCE((SELECT SUM(r.reservation_microusd) FROM {_db.Dialect.Table("budget_reservation")} r WHERE r.dataset_id=d.dataset_id AND r.status<>'completed'),0)
            FROM {_db.Dialect.Table("control_dataset")} d WHERE d.dataset_id=@dataset
            """, ("@dataset", datasetId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(ControlDb.Usd(reader.GetInt64(0)), ControlDb.Usd(reader.GetInt64(1)), ControlDb.Usd(reader.GetInt64(2)));
    }

    private async Task<BudgetReservation?> ReadReservationAsync(DbConnection connection, DbTransaction transaction,
        string requestId, CancellationToken ct)
    {
        using var command = ControlDb.Command(connection, transaction, $"""
            SELECT dataset_id,request_id,project_id,run_id,provider,model,reservation_microusd,status,actual_microusd
            FROM {_db.Dialect.Table("budget_reservation")} WHERE request_id=@request
            """, ("@request", requestId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), ControlDb.Usd(reader.GetInt64(6))), reader.GetString(7),
            reader.IsDBNull(8) ? null : ControlDb.Usd(reader.GetInt64(8)));
    }
}
