using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v30 — followup_draft.disposition + disposition_detail: the
/// completion-time follow-up triage records what happened to each
/// draft (materialized-as / merged-into / epic / discarded + the
/// target id or reason). Workload profile only. Idempotent.
/// </summary>
public sealed class M030FollowUpDraftDisposition : ISqlServerMigration
{
    public int Version => 30;
    public string Name => "followup-draft-disposition";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var table = dialect.Table("followup_draft");
        var q = dialect.Qualifier;
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'followup_draft' AND c.name = 'disposition')
                ALTER TABLE {table} ADD disposition NVARCHAR(32) NULL;
            """);
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'followup_draft' AND c.name = 'disposition_detail')
                ALTER TABLE {table} ADD disposition_detail NVARCHAR(512) NULL;
            """);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
