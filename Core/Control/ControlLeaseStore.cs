using System.Data;
using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core.Control;

public sealed record ControlLeaseState(string DatasetId, string? OwnerInstanceId, long Epoch,
    DateTimeOffset? LastHeartbeatUtc, DateTimeOffset DatabaseUtcNow, decimal BudgetLimitUsd);

/// <summary>No timer-based stealing. A takeover requires the caller's explicit previous-owner comparison.</summary>
public interface IControlLeaseStore
{
    Task InitializeDatasetAsync(string datasetId, decimal budgetLimitUsd, CancellationToken ct = default);
    Task<ControlLeaseState?> ReadAsync(string datasetId, CancellationToken ct = default);
    Task<ControlLeaseState?> TryAcquireAsync(string datasetId, string instanceId, string operationId,
        string? expectedPreviousOwner = null, CancellationToken ct = default);
    Task<bool> RenewAsync(string datasetId, string instanceId, long epoch, CancellationToken ct = default);
    Task<bool> ReleaseAsync(string datasetId, string instanceId, long epoch, CancellationToken ct = default);
    Task<bool> IsOwnerAsync(string datasetId, string instanceId, long epoch, CancellationToken ct = default);
}

public sealed class ControlLeaseStore : IControlLeaseStore
{
    private readonly IDbConnectionFactory _db;
    public ControlLeaseStore(IDbConnectionFactory db) { ControlDb.ValidateFactory(db); _db = db; }

    public async Task InitializeDatasetAsync(string datasetId, decimal budgetLimitUsd, CancellationToken ct = default)
    {
        ControlDb.ValidateId(datasetId, nameof(datasetId));
        if (budgetLimitUsd * 1_000_000m != decimal.Truncate(budgetLimitUsd * 1_000_000m))
            throw new ArgumentException("Budget limit must be an exact number of microdollars.", nameof(budgetLimitUsd));
        var limit = ControlDb.Money(budgetLimitUsd);
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var table = _db.Dialect.Table("control_dataset");
        // UPDLOCK+HOLDLOCK covers the absent singleton key as well as an existing row.
        var hint = _db.Provider == ForgeDbProvider.SqlServer ? " WITH (UPDLOCK,HOLDLOCK)" : "";
        using var read = ControlDb.Command(connection, transaction,
            $"SELECT dataset_id,budget_limit_microusd FROM {table}{hint} WHERE singleton=1");
        bool exists;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            exists = await reader.ReadAsync(ct);
            if (exists && (reader.GetString(0) != datasetId || reader.GetInt64(1) != limit))
                throw new InvalidOperationException("Dataset identity and initial budget are immutable; use a fresh database for a new dataset.");
        }
        if (!exists)
        {
            using var insert = ControlDb.Command(connection, transaction,
                $"INSERT INTO {table}(singleton,dataset_id,budget_limit_microusd,epoch) VALUES(1,@dataset,@limit,0)",
                ("@dataset", datasetId), ("@limit", limit));
            await insert.ExecuteNonQueryAsync(ct);
            using var budget = ControlDb.Command(connection, transaction,
                $"INSERT INTO {_db.Dialect.Table("budget_lock")}(dataset_id,lock_version) VALUES(@dataset,0)",
                ("@dataset", datasetId));
            await budget.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<ControlLeaseState?> ReadAsync(string datasetId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        return await ReadAsync(connection, null, datasetId, ct);
    }

    private async Task<ControlLeaseState?> ReadAsync(DbConnection connection, DbTransaction? transaction,
        string datasetId, CancellationToken ct)
    {
        using var command = ControlDb.Command(connection, transaction,
            $"SELECT dataset_id,owner_instance_id,epoch,heartbeat_at,{ControlDb.Now(_db)},budget_limit_microusd FROM {_db.Dialect.Table("control_dataset")} WHERE dataset_id=@dataset",
            ("@dataset", datasetId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : ControlDb.Time(reader.GetString(3)), ControlDb.Time(reader.GetString(4)),
            ControlDb.Usd(reader.GetInt64(5)));
    }

    public async Task<ControlLeaseState?> TryAcquireAsync(string datasetId, string instanceId, string operationId,
        string? expectedPreviousOwner = null, CancellationToken ct = default)
    {
        ControlDb.ValidateId(datasetId, nameof(datasetId));
        ControlDb.ValidateId(instanceId, nameof(instanceId));
        ControlDb.ValidateId(operationId, nameof(operationId));
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (_db.Provider == ForgeDbProvider.SqlServer)
        {
            // The initializer owns an Exclusive session lock on this resource.
            // Taking Shared before ownership prevents activation between its
            // no-owner probe and DDL. Committing ownership releases Shared;
            // an initializer arriving afterward sees the owner and refuses.
            using var migrationLock = ControlDb.Command(connection, transaction, """
                DECLARE @result int;
                EXEC @result=sp_getapplock @Resource=N'forge-schema-migration',
                    @LockMode='Shared',@LockOwner='Transaction',@LockTimeout=0;
                SELECT @result;
                """);
            var result = await migrationLock.ExecuteScalarAsync(ct);
            if (result is null || result is DBNull || Convert.ToInt32(result) < 0) return null;
        }
        if (!await ControlDb.LockDatasetAsync(_db, connection, transaction, datasetId, ct)) return null;
        var current = (await ReadAsync(connection, transaction, datasetId, ct))!;
        using var operation = ControlDb.Command(connection, transaction,
            $"SELECT dataset_id,instance_id,epoch FROM {_db.Dialect.Table("control_operation")} WHERE operation_id=@operation",
            ("@operation", operationId));
        await using (var reader = await operation.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var same = reader.GetString(0) == datasetId && reader.GetString(1) == instanceId
                    && reader.GetInt64(2) == current.Epoch && current.OwnerInstanceId == instanceId;
                // No mutation on replay, and never transfer an old operation to a replacement pod.
                return same ? current : null;
            }
        }
        if (!string.Equals(current.OwnerInstanceId, expectedPreviousOwner, StringComparison.Ordinal)) return null;
        var epoch = checked(current.Epoch + 1);
        using var update = ControlDb.Command(connection, transaction,
            $"UPDATE {_db.Dialect.Table("control_dataset")} SET owner_instance_id=@owner,epoch=@epoch,heartbeat_at={ControlDb.Now(_db)} WHERE dataset_id=@dataset",
            ("@owner", instanceId), ("@epoch", epoch), ("@dataset", datasetId));
        await update.ExecuteNonQueryAsync(ct);
        using var insert = ControlDb.Command(connection, transaction,
            $"INSERT INTO {_db.Dialect.Table("control_operation")}(operation_id,dataset_id,instance_id,epoch,acquired_at) VALUES(@operation,@dataset,@owner,@epoch,{ControlDb.Now(_db)})",
            ("@operation", operationId), ("@dataset", datasetId), ("@owner", instanceId), ("@epoch", epoch));
        await insert.ExecuteNonQueryAsync(ct);
        var acquired = await ReadAsync(connection, transaction, datasetId, ct);
        await transaction.CommitAsync(ct);
        return acquired;
    }

    public Task<bool> RenewAsync(string datasetId, string instanceId, long epoch, CancellationToken ct = default)
        => UpdateOwnershipAsync(datasetId, instanceId, epoch, release: false, ct);

    public Task<bool> ReleaseAsync(string datasetId, string instanceId, long epoch, CancellationToken ct = default)
        => UpdateOwnershipAsync(datasetId, instanceId, epoch, release: true, ct);

    private async Task<bool> UpdateOwnershipAsync(string datasetId, string instanceId, long epoch, bool release, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct);
        using var command = ControlDb.Command(connection, null,
            $"UPDATE {_db.Dialect.Table("control_dataset")} SET {(release ? "owner_instance_id=NULL,heartbeat_at=NULL" : $"heartbeat_at={ControlDb.Now(_db)}")} WHERE dataset_id=@dataset AND owner_instance_id=@owner AND epoch=@epoch",
            ("@dataset", datasetId), ("@owner", instanceId), ("@epoch", epoch));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> IsOwnerAsync(string datasetId, string instanceId, long epoch, CancellationToken ct = default)
    {
        var state = await ReadAsync(datasetId, ct);
        return state is not null && state.OwnerInstanceId == instanceId && state.Epoch == epoch;
    }
}
