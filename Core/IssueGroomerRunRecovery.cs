using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class IssueGroomerRunStore
{
    private static DbCommand RecoveryCommand(DbConnection connection, DbTransaction? transaction, string sql, params (string, object?)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in values) command.AddParam(key, value ?? DBNull.Value);
        return command;
    }

    private async Task<(long Id, string Status)?> LatestAsync(DbConnection connection, DbTransaction transaction, string specId, CancellationToken ct)
    {
        using var command = RecoveryCommand(connection, transaction,
            $"SELECT {_db.Dialect.Top(1)}id,status FROM {T("issue_groomer_run")}{(_db.Provider == ForgeDbProvider.SqlServer ? " WITH (UPDLOCK,HOLDLOCK)" : "")} WHERE spec_id=@spec ORDER BY id DESC{_db.Dialect.Limit(1)}", ("@spec", specId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (Convert.ToInt64(reader.GetValue(0)), reader.GetString(1)) : null;
    }

    public async Task<long> QueueAsync(string specId, bool retry, long? expectedRunId = null, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var latest = await LatestAsync(connection, transaction, specId, ct);
        if (latest?.Status is "started" or "queued") throw new InvalidOperationException("A grooming run is already active or queued.");
        if (!retry && latest?.Status is "failed" or "needs_review")
            throw new InvalidOperationException("An interrupted or failed attempt requires explicit safe retry with its current run ID.");
        if (retry && (latest?.Id != expectedRunId || latest is { Status: not ("failed" or "needs_review") }))
            throw new InvalidOperationException("The grooming attempt changed; refresh its recovery state before retrying.");
        using var insert = RecoveryCommand(connection, transaction,
            _db.Provider == ForgeDbProvider.SqlServer
                ? $"INSERT INTO {T("issue_groomer_run")}(ts,spec_id,trigger_kind,status) OUTPUT INSERTED.id VALUES(@now,@spec,@trigger,'queued')"
                : $"INSERT INTO {T("issue_groomer_run")}(ts,spec_id,trigger_kind,status) VALUES(@now,@spec,@trigger,'queued') RETURNING id",
            ("@now", IssueStore.DateFormatTime(DateTime.UtcNow)), ("@spec", specId), ("@trigger", retry ? "retry" : "manual"));
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        await transaction.CommitAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<IssueGroomerRun>> ListQueuedAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        using var command = RecoveryCommand(connection, null,
            $"SELECT {_db.Dialect.Top(50)}id,ts,spec_id,trigger_kind FROM {T("issue_groomer_run")} WHERE status='queued' ORDER BY id{_db.Dialect.Limit(50)}");
        var result = new List<IssueGroomerRun>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(Convert.ToInt64(reader.GetValue(0)), IssueStore.ParseTime(reader.GetString(1)),
            reader.GetString(2), ParseTrigger(reader.GetString(3)), GroomerRunStatus.Queued, 0, 0, null, 0));
        return result;
    }

    public async Task<bool> TryClaimQueuedAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        using var command = RecoveryCommand(connection, null,
            $"UPDATE {T("issue_groomer_run")} SET status='started' WHERE id=@id AND status='queued'", ("@id", id));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IssueGroomerRun?> TryStartScheduledAsync(string specId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var latest = await LatestAsync(connection, transaction, specId, ct);
        if (latest?.Status is "started" or "queued" or "needs_review" or "failed") return null;
        var now = DateTime.UtcNow;
        using var insert = RecoveryCommand(connection, transaction,
            _db.Provider == ForgeDbProvider.SqlServer
                ? $"INSERT INTO {T("issue_groomer_run")}(ts,spec_id,trigger_kind,status) OUTPUT INSERTED.id VALUES(@now,@spec,'scheduled','started')"
                : $"INSERT INTO {T("issue_groomer_run")}(ts,spec_id,trigger_kind,status) VALUES(@now,@spec,'scheduled','started') RETURNING id",
            ("@now", IssueStore.DateFormatTime(now)), ("@spec", specId));
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        await transaction.CommitAsync(ct);
        return new(id, now, specId, GroomerTriggerKind.Scheduled, GroomerRunStatus.Started, 0, 0, null, 0);
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        using var command = RecoveryCommand(connection, null,
            $"UPDATE {T("issue_groomer_run")} SET status='needs_review',error='Grooming was interrupted. The previous paid request may have completed; explicitly retry to resume the preserved decomposition.' WHERE status='started'");
        await command.ExecuteNonQueryAsync(ct);
    }
}
