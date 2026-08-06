using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v28 — followup_draft: follow-ups are TRACKED during a sprint (no
/// live task rows) and materialized into real tasks at sprint
/// completion (operator model 2026-07-31: no follow-up work against
/// unmerged code; feedback returns to the worker). Workload profile
/// only. Fully idempotent.
/// </summary>
public sealed class M028FollowUpDraft : ISqlServerMigration
{
    public int Version => 28;
    public string Name => "followup-draft";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var q = dialect.Qualifier;
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'followup_draft')
            CREATE TABLE {dialect.Table("followup_draft")} (
                id              INT IDENTITY(1,1) PRIMARY KEY,
                sprint_id       NVARCHAR(128) NULL,
                source_issue_id NVARCHAR(128) NOT NULL,
                source_role     NVARCHAR(64) NOT NULL,
                title           NVARCHAR(512) NOT NULL,
                description     NVARCHAR(MAX) NOT NULL,
                priority        INT NOT NULL DEFAULT 3,
                blocks_issue_id NVARCHAR(128) NULL,
                created_at      NVARCHAR(40) NOT NULL,
                consumed_at     NVARCHAR(40) NULL
            );
            """);
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'followup_draft' AND i.name = 'ix_followup_draft_open')
            CREATE INDEX ix_followup_draft_open ON {dialect.Table("followup_draft")} (consumed_at, sprint_id);
            """);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
