using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v24 — profile split. The pre-split uniform DDL gave every project
/// schema the full 31-table set, with the registry (project/secret)
/// physically inside the 'default' project's schema. This migration:
///
/// Core profile (schema 'core'):
///   1. Copies registry rows proj_default.project/secret -> core
///      (only when core is empty — a repaired or fresh database skips).
///   2. Drops the proj_default schema entirely (FKs, tables, schema).
///
/// Workload profile (schema proj_&lt;id&gt;):
///   3. Drops the registry tables (project/secret/agent/skill) that the
///      uniform DDL created in workload schemas.
///
/// agent/skill rows are NOT copied: they reseed from files at startup
/// (BulkUpsertFromAgentFilesAsync / SkillSeeder).
/// Fully idempotent — every statement is existence-guarded.
/// </summary>
public sealed class M024ProfileSplit : ISqlServerMigration
{
    public int Version => 24;
    public string Name => "profile-split";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile == ForgeSchemaProfile.Core) ApplyCore(conn, dialect);
        else ApplyWorkload(conn, dialect);
    }

    private static void ApplyCore(DbConnection conn, SqlServerDialect dialect)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'proj_default')
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM {dialect.Table("project")})
                BEGIN
                    INSERT INTO {dialect.Table("project")} (id, name, repo_url, default_branch, local_path, created_at, updated_at, last_synced_at, last_sync_error, roles_json)
                    SELECT id, name, repo_url, default_branch, local_path, created_at, updated_at, last_synced_at, last_sync_error, roles_json
                    FROM proj_default.project;
                    INSERT INTO {dialect.Table("secret")} (id, project_id, kind, ciphertext, created_at, updated_at)
                    SELECT id, project_id, kind, ciphertext, created_at, updated_at
                    FROM proj_default.secret;
                END
                DECLARE @sql NVARCHAR(MAX) = N'';
                SELECT @sql += N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
                FROM sys.foreign_keys fk
                JOIN sys.tables t ON fk.parent_object_id = t.object_id
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE s.name = 'proj_default';
                SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
                FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE s.name = 'proj_default';
                EXEC sp_executesql @sql;
                DROP SCHEMA proj_default;
            END
            """;
        cmd.ExecuteNonQuery();
    }

    private static void ApplyWorkload(DbConnection conn, SqlServerDialect dialect)
    {
        using var cmd = conn.CreateCommand();
        var q = dialect.Qualifier;
        // Dependency order: secret -> project, skill -> agent.
        cmd.CommandText = $"""
            IF OBJECT_ID('[{q}].[secret]', 'U') IS NOT NULL DROP TABLE [{q}].[secret];
            IF OBJECT_ID('[{q}].[project]', 'U') IS NOT NULL DROP TABLE [{q}].[project];
            IF OBJECT_ID('[{q}].[skill]', 'U') IS NOT NULL DROP TABLE [{q}].[skill];
            IF OBJECT_ID('[{q}].[agent]', 'U') IS NOT NULL DROP TABLE [{q}].[agent];
            """;
        cmd.ExecuteNonQuery();
    }
}
