using System.Data.Common;
using System.Globalization;
using Forge.Core.Db;

namespace Forge.Core.Control;

internal static class ControlDb
{
    internal static void ValidateFactory(IDbConnectionFactory db)
    {
        if (db.Provider == ForgeDbProvider.SqlServer && db.Profile != ForgeSchemaProfile.Core)
            throw new ArgumentException("Control state must use the registry/core SQL schema.", nameof(db));
    }

    internal static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ArgumentException("Identity must contain 1-128 nonblank characters.", name);
    }

    internal static string Now(IDbConnectionFactory db) => db.Provider == ForgeDbProvider.SqlServer
        ? "CONVERT(NVARCHAR(33), SYSUTCDATETIME(), 126) + 'Z'"
        : "strftime('%Y-%m-%dT%H:%M:%fZ','now')";

    internal static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.AddParam(name, value);
        return command;
    }

    // Ownership operations take the same row's write lock BEFORE reading.
    // Budget operations use their own row, below, with narrower SQL privileges.
    internal static async Task<bool> LockDatasetAsync(IDbConnectionFactory db, DbConnection connection,
        DbTransaction transaction, string datasetId, CancellationToken ct)
    {
        using var command = Command(connection, transaction,
            $"UPDATE {db.Dialect.Table("control_dataset")} SET epoch=epoch WHERE singleton=1 AND dataset_id=@dataset",
            ("@dataset", datasetId));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    // Paid workers need budget-table authority, never UPDATE on controller ownership.
    internal static async Task<bool> LockBudgetAsync(IDbConnectionFactory db, DbConnection connection,
        DbTransaction transaction, string datasetId, CancellationToken ct)
    {
        using var command = Command(connection, transaction,
            $"UPDATE {db.Dialect.Table("budget_lock")} SET lock_version=lock_version WHERE dataset_id=@dataset",
            ("@dataset", datasetId));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    internal static long Money(decimal usd, bool allowZero = false)
    {
        if (usd < 0 || (!allowZero && usd == 0)) throw new ArgumentOutOfRangeException(nameof(usd));
        return checked((long)decimal.Ceiling(usd * 1_000_000m));
    }

    internal static decimal Usd(long microUsd) => microUsd / 1_000_000m;
    internal static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
