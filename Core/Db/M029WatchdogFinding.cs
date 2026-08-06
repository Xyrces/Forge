using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v29 — watchdog_finding: the watchdog scanner's deduped findings
/// (alert-only v1 — one open row per (kind, target); resolves when
/// the condition clears). Workload profile only. Idempotent.
/// </summary>
public sealed class M029WatchdogFinding : ISqlServerMigration
{
    public int Version => 29;
    public string Name => "watchdog-finding";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var q = dialect.Qualifier;
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'watchdog_finding')
            CREATE TABLE {dialect.Table("watchdog_finding")} (
                id            INT IDENTITY(1,1) PRIMARY KEY,
                kind          NVARCHAR(64)  NOT NULL,
                target_id     NVARCHAR(128) NOT NULL,
                severity      NVARCHAR(16)  NOT NULL,
                detail        NVARCHAR(MAX) NOT NULL,
                status        NVARCHAR(16)  NOT NULL,
                first_seen_at NVARCHAR(40)  NOT NULL,
                last_seen_at  NVARCHAR(40)  NOT NULL,
                resolved_at   NVARCHAR(40)  NULL
            );
            """);
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'watchdog_finding' AND i.name = 'ix_watchdog_finding_open')
            CREATE INDEX ix_watchdog_finding_open ON {dialect.Table("watchdog_finding")} (status, kind, target_id);
            """);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
