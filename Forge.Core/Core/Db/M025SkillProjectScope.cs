using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v25 — per-project skills. Adds <c>skill.project_id</c> (NULL = global)
/// and <c>skill.source</c> ('forge' = UI-owned, 'repo' = imported from a
/// project's .kilo/skills and read-only in the dashboard), and re-keys the
/// uniqueness from (name, agent_id) to (name, project_id) so a global skill
/// and a project skill may share a name (the project copy wins at load
/// time). SQL Server unique constraints treat NULL as a value, so the old
/// (name, agent_id) constraint would reject a same-named global+project
/// pair (both agent_id NULL) — it must be dropped, by dynamic name lookup.
/// skill lives only in the Core profile schema (M024 dropped the workload
/// copies); the workload profile is a no-op. Fully idempotent. Statements
/// run as separate commands: a batch is compiled before it executes, so a
/// column added in one statement cannot be referenced by the next within
/// the same batch.
/// </summary>
public sealed class M025SkillProjectScope : ISqlServerMigration
{
    public int Version => 25;
    public string Name => "skill-project-scope";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Core) return;
        var table = dialect.Table("skill");
        var q = dialect.Qualifier;

        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'skill' AND c.name = 'project_id')
                ALTER TABLE {table} ADD project_id NVARCHAR(128) NULL;
            """);
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'skill' AND c.name = 'source')
                ALTER TABLE {table} ADD source NVARCHAR(16) NOT NULL DEFAULT 'forge';
            """);
        Exec(conn, $"""
            DECLARE @uq NVARCHAR(128);
            SELECT @uq = kc.name
            FROM sys.key_constraints kc
            JOIN sys.tables t ON kc.parent_object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.index_columns ic ON ic.object_id = t.object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c ON c.object_id = t.object_id AND c.column_id = ic.column_id
            WHERE s.name = '{q}' AND t.name = 'skill' AND kc.type = 'UQ' AND c.name = 'agent_id';
            IF @uq IS NOT NULL EXEC(N'ALTER TABLE {table} DROP CONSTRAINT [' + @uq + N']');
            """);
        Exec(conn, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.key_constraints kc JOIN sys.tables t ON kc.parent_object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'skill' AND kc.name = 'uq_skill_name_project')
                ALTER TABLE {table} ADD CONSTRAINT uq_skill_name_project UNIQUE (name, project_id);
            """);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
