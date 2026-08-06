using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v32 — agent_run token observability: input/output/cache-read
/// totals persisted at finish + the live context size updated per
/// heartbeat (operator 2026-08-06: quota burn was invisible — only
/// chars÷4 proxies existed; AgentRunResult even hardcoded zeros).
/// Workload profile only. Idempotent.
/// </summary>
public sealed class M032AgentRunTokens : ISqlServerMigration
{
    public int Version => 32;
    public string Name => "agent-run-tokens";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var table = dialect.Table("agent_run");
        var q = dialect.Qualifier;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'agent_run' AND c.name = 'input_tokens')
            BEGIN
                ALTER TABLE {table} ADD input_tokens BIGINT NULL;
                ALTER TABLE {table} ADD output_tokens BIGINT NULL;
                ALTER TABLE {table} ADD cache_read_tokens BIGINT NULL;
                ALTER TABLE {table} ADD current_context_tokens BIGINT NULL;
            END
            """;
        cmd.ExecuteNonQuery();
    }
}
