using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v35 — failure_triage: the failure ledger (phase 1 observability —
/// what failed, the deterministic signature/classification, who
/// cleared it, whether the redispatch held). escalated_* columns are
/// phase-3 placeholders so per-task model escalation needs no
/// migration later. Workload profile only. Fully idempotent.
/// </summary>
public sealed class M035FailureTriage : ISqlServerMigration
{
    public int Version => 35;
    public string Name => "failure-triage";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var q = dialect.Qualifier;
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'failure_triage')
            CREATE TABLE {dialect.Table("failure_triage")} (
                id                  INT IDENTITY(1,1) PRIMARY KEY,
                task_id             NVARCHAR(128) NOT NULL,
                failed_at           NVARCHAR(40)  NOT NULL,
                signature           NVARCHAR(64)  NOT NULL,
                classification      NVARCHAR(64)  NOT NULL,
                error_excerpt       NVARCHAR(600) NULL,
                action              NVARCHAR(64)  NULL,
                actor               NVARCHAR(64)  NULL,
                acted_at            NVARCHAR(40)  NULL,
                outcome             NVARCHAR(32)  NULL,
                escalated_provider  NVARCHAR(64)  NULL,
                escalated_model     NVARCHAR(128) NULL
            );
            """);
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'failure_triage' AND i.name = 'ix_failure_triage_task')
            CREATE INDEX ix_failure_triage_task ON {dialect.Table("failure_triage")} (task_id, outcome);
            """);
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'failure_triage' AND i.name = 'ix_failure_triage_signature')
            CREATE INDEX ix_failure_triage_signature ON {dialect.Table("failure_triage")} (signature, failed_at);
            """);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
