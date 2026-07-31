using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v26 — agent_run.phase + agent_run.resumed_session. The phase column
/// carries the run's live phase label (plan gate / implementing /
/// verifying n/3 / reviewing) written by the heartbeat; resumed_session
/// marks runs that resumed a persisted MAF session (pause/resume
/// architecture) instead of starting cold. agent_run lives only in the
/// Workload profile schemas (M024 split); the Core profile is a no-op.
/// Fully idempotent; statements run as separate commands (a batch is
/// compiled before it executes, so a column added in one statement
/// cannot be referenced by the next within the same batch).
/// </summary>
public sealed class M026AgentRunPhase : ISqlServerMigration
{
    public int Version => 26;
    public string Name => "agent-run-phase";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var table = dialect.Table("agent_run");
        var q = dialect.Qualifier;

        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'agent_run' AND c.name = 'phase')
                ALTER TABLE {table} ADD phase NVARCHAR(64) NULL;
            """);
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'agent_run' AND c.name = 'resumed_session')
                ALTER TABLE {table} ADD resumed_session INT NULL;
            """);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
