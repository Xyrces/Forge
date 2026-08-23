using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v36 — followup_draft.task_type: follow-ups carry the task type that
/// routes the materialized task to the right role (client work →
/// clientdev, playtest evidence → qa). Without it every follow-up was
/// born type='task' → coredev, and client-scope follow-ups died at the
/// plan-territory gate (observed live 2026-08-23: porthorizon task-752,
/// filed by CoreDev, hollow-closed after the gate blocked every plan).
/// Workload profile only. Idempotent.
/// </summary>
public sealed class M036FollowUpDraftTaskType : ISqlServerMigration
{
    public int Version => 36;
    public string Name => "followup-draft-task-type";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{dialect.Qualifier}' AND t.name = 'followup_draft' AND c.name = 'task_type')
                ALTER TABLE {dialect.Table("followup_draft")} ADD task_type NVARCHAR(64) NULL;
            """);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
