using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v27 — agent_run.project_id. Run attribution for the global run
/// registry: agent_run stays a single registry across projects (all
/// runners write to the primary store), so the dashboard breaks runs
/// down by project via this column instead of per-project stores.
/// NULL = historical pre-v27 run. agent_run lives only in the Workload
/// profile schemas (M024 split); the Core profile is a no-op. Fully
/// idempotent.
/// </summary>
public sealed class M027AgentRunProject : ISqlServerMigration
{
    public int Version => 27;
    public string Name => "agent-run-project";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var table = dialect.Table("agent_run");
        var q = dialect.Qualifier;

        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'agent_run' AND c.name = 'project_id')
                ALTER TABLE {table} ADD project_id NVARCHAR(128) NULL;
            """);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
