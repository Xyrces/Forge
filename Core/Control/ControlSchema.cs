using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core.Control;

/// <summary>Global controller ownership and paid-request liability, in the registry schema.</summary>
public static class ControlSchema
{
    public static void Create(DbConnection connection, ISqlDialect dialect)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Ddl(dialect);
        command.ExecuteNonQuery();
    }

    public static string Ddl(ISqlDialect dialect)
    {
        var sqlServer = dialect.Provider == ForgeDbProvider.SqlServer;
        var key = sqlServer ? "NVARCHAR(128) COLLATE Latin1_General_100_BIN2" : "TEXT";
        var number = sqlServer ? "BIGINT" : "INTEGER";
        var time = sqlServer ? "NVARCHAR(40)" : "TEXT";
        string Create(string table, string columns) => sqlServer
            ? $"IF OBJECT_ID(N'{dialect.Table(table)}', N'U') IS NULL CREATE TABLE {dialect.Table(table)} ({columns});"
            : $"CREATE TABLE IF NOT EXISTS {table} ({columns});";
        return Create("control_dataset", $"""
            singleton {number} NOT NULL PRIMARY KEY CHECK (singleton = 1),
            dataset_id {key} NOT NULL UNIQUE,
            budget_limit_microusd {number} NOT NULL CHECK (budget_limit_microusd > 0),
            owner_instance_id {key} NULL,
            epoch {number} NOT NULL CHECK (epoch >= 0),
            heartbeat_at {time} NULL
            """) + Create("control_operation", $"""
            operation_id {key} NOT NULL PRIMARY KEY,
            dataset_id {key} NOT NULL,
            instance_id {key} NOT NULL,
            epoch {number} NOT NULL,
            acquired_at {time} NOT NULL
            """) + Create("budget_lock", $"""
            dataset_id {key} NOT NULL PRIMARY KEY,
            lock_version {number} NOT NULL
            """) + Create("budget_reservation", $"""
            request_id {key} NOT NULL PRIMARY KEY,
            dataset_id {key} NOT NULL,
            project_id {key} NOT NULL,
            run_id {key} NOT NULL,
            provider {key} NOT NULL,
            model {key} NOT NULL,
            reservation_microusd {number} NOT NULL CHECK (reservation_microusd > 0),
            actual_microusd {number} NULL CHECK (actual_microusd >= 0),
            usage_json {(sqlServer ? "NVARCHAR(MAX)" : "TEXT")} NULL,
            pricing_basis {key} NULL,
            status {key} NOT NULL CHECK (status IN ('reserved', 'uncertain', 'completed')),
            created_at {time} NOT NULL,
            updated_at {time} NOT NULL
            """) + ReconciliationDdl(dialect);
    }
    public static string ReconciliationDdl(ISqlDialect dialect)
    {
        var sql = dialect.Provider == ForgeDbProvider.SqlServer;
        var key = sql ? "NVARCHAR(128) COLLATE Latin1_General_100_BIN2" : "TEXT";
        var number = sql ? "BIGINT" : "INTEGER";
        var text = sql ? "NVARCHAR(MAX)" : "TEXT";
        string Create(string table, string columns) => sql
            ? $"IF OBJECT_ID(N'{dialect.Table(table)}', N'U') IS NULL CREATE TABLE {dialect.Table(table)} ({columns});"
            : $"CREATE TABLE IF NOT EXISTS {table} ({columns});";
        return Create("budget_reconciliation", $"""
            reconciliation_id {key} NOT NULL PRIMARY KEY,
            dataset_id {key} NOT NULL,
            original_microusd {number} NOT NULL CHECK(original_microusd >= 0),
            reported_microusd {number} NOT NULL CHECK(reported_microusd >= 0),
            source {key} NOT NULL,
            operator_name {key} NOT NULL,
            evidence_sha256 {key} NOT NULL,
            manifest_json {text} NOT NULL,
            created_at {key} NOT NULL
            """) + Create("budget_reconciliation_request", $"""
            request_id {key} NOT NULL PRIMARY KEY,
            reconciliation_id {key} NOT NULL,
            dataset_id {key} NOT NULL,
            original_microusd {number} NOT NULL CHECK(original_microusd >= 0)
            """);
    }

}
