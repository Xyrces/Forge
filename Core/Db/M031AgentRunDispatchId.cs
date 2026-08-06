using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v31 — agent_run.dispatch_id: the correlation id threading an
/// engineering dispatch through claim → worktree → run → push
/// (postmortem tracing, operator 2026-08-01). Workload profile only.
/// Idempotent.
/// </summary>
public sealed class M031AgentRunDispatchId : ISqlServerMigration
{
    public int Version => 31;
    public string Name => "agent-run-dispatch-id";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var table = dialect.Table("agent_run");
        var q = dialect.Qualifier;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'agent_run' AND c.name = 'dispatch_id')
                ALTER TABLE {table} ADD dispatch_id NVARCHAR(64) NULL;
            """;
        cmd.ExecuteNonQuery();
    }
}
