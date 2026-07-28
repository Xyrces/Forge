using Microsoft.Data.Sqlite;

namespace Forge.Core.Db;

/// <summary>
/// One-shot state migration: SQLite files -> SQL Server (Azure SQL).
/// Scope per the operator-approved cutover model: the project registry,
/// per-project secret ciphertext (opaque — the DataProtection keyring
/// stays on the machine, so ciphertext migrates as-is), and all memory
/// keys (vision, workflow/live, gate holds, model prefs, sprint memory).
/// Issue/spec/sprint/agent-run history stays behind unless
/// <see cref="MigrateOptions.IncludeOpenWork"/> is set, in which case
/// non-terminal issues carry their dep edges, linked specs (+ versions
/// and extraction rows), and Active sprints so in-flight work survives.
///
/// Idempotent: every write is keyed upsert (MERGE / IF NOT EXISTS), so
/// re-running after a rehearsal or partial failure is safe. The target
/// schema is created by constructing <see cref="IssueStore"/> against
/// the per-project factory before any rows are copied.
/// </summary>
public static class StateMigrator
{
    public sealed record ProjectSource(string ProjectId, string IssuesDbPath, string? MemoryDbPath);

    public sealed record MigrateOptions(bool IncludeOpenWork = false);

    public static async Task<List<string>> MigrateAsync(
        IReadOnlyList<ProjectSource> sources,
        string targetConnectionString,
        MigrateOptions options,
        CancellationToken ct = default)
    {
        var report = new List<string>();
        foreach (var src in sources)
        {
            var schema = ForgeDb.SchemaForProject(src.ProjectId);
            var target = ForgeDb.SqlServer(targetConnectionString, schema);
            // Fresh-creates the schema + all tables (no-op when present).
            _ = new IssueStore(target);
            report.Add($"project '{src.ProjectId}': target schema [{schema}] ensured");

            await CopyRegistryAsync(src, target, report, ct);
            await CopyMemoryAsync(src, target, report, ct);
            if (options.IncludeOpenWork)
                await CopyOpenWorkAsync(src, target, report, ct);
        }
        return report;
    }

    /// <summary>
    /// Rehearsal helper: drops every table in the given project schemas,
    /// then the schemas themselves. Destructive — call sites gate this
    /// behind an explicit --reset flag.
    /// </summary>
    public static async Task ResetAsync(
        string targetConnectionString,
        IReadOnlyList<string> projectIds,
        CancellationToken ct = default)
    {
        foreach (var pid in projectIds)
        {
            var schema = ForgeDb.SchemaForProject(pid);
            var target = ForgeDb.SqlServer(targetConnectionString, schema);
            await using var conn = await target.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                DECLARE @sql NVARCHAR(MAX) = N'';
                SELECT @sql += N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
                FROM sys.foreign_keys fk
                JOIN sys.tables t ON fk.parent_object_id = t.object_id
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE s.name = @schema;
                SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
                FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE s.name = @schema;
                EXEC sp_executesql @sql;
                IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schema)
                    EXEC('DROP SCHEMA [{schema}]');
                """;
            cmd.AddParam("@schema", schema);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // --- registry: project + secret (source: the 'default' project's issues.db) ---

    private static async Task CopyRegistryAsync(
        ProjectSource src, IDbConnectionFactory target, List<string> report, CancellationToken ct)
    {
        var d = target.Dialect;
        await using var conn = await target.OpenAsync(ct);

        var projects = await ReadSqlite(src.IssuesDbPath,
            "SELECT id, name, repo_url, default_branch, local_path, created_at, updated_at, last_synced_at, last_sync_error, roles_json FROM project", ct,
            rd => new object?[]
            {
                rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.GetString(5), rd.GetString(6),
                rd.IsDBNull(7) ? null : rd.GetString(7),
                rd.IsDBNull(8) ? null : rd.GetString(8),
                rd.IsDBNull(9) ? null : rd.GetString(9),
            });
        foreach (var r in projects)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                MERGE {d.Table("project")} WITH (HOLDLOCK) AS t
                USING (SELECT @id AS id) AS s ON t.id = s.id
                WHEN MATCHED THEN UPDATE SET name=@name, repo_url=@url, default_branch=@branch,
                    local_path=@path, last_synced_at=@synced, last_sync_error=@err, roles_json=@roles, updated_at=@updated
                WHEN NOT MATCHED THEN INSERT (id, name, repo_url, default_branch, local_path, created_at, updated_at, last_synced_at, last_sync_error, roles_json)
                    VALUES (@id, @name, @url, @branch, @path, @created, @updated, @synced, @err, @roles);
                """;
            cmd.AddParam("@id", r[0]); cmd.AddParam("@name", r[1]); cmd.AddParam("@url", r[2]);
            cmd.AddParam("@branch", r[3]); cmd.AddParam("@path", r[4]); cmd.AddParam("@created", r[5]);
            cmd.AddParam("@updated", r[6]); cmd.AddParam("@synced", r[7]); cmd.AddParam("@err", r[8]);
            cmd.AddParam("@roles", r[9]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (projects.Count > 0) report.Add($"project '{src.ProjectId}': registry projects={projects.Count}");

        if (!SqliteTableExists(src.IssuesDbPath, "secret"))
            return;
        var secrets = await ReadSqlite(src.IssuesDbPath,
            "SELECT id, project_id, kind, ciphertext, created_at, updated_at FROM secret", ct,
            rd => new object?[]
            {
                rd.GetString(0), rd.GetString(1), rd.GetString(2),
                (byte[])rd.GetValue(3), rd.GetString(4), rd.GetString(5),
            });
        foreach (var r in secrets)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                MERGE {d.Table("secret")} WITH (HOLDLOCK) AS t
                USING (SELECT @pid AS project_id, @kind AS kind) AS s
                    ON t.project_id = s.project_id AND t.kind = s.kind
                WHEN MATCHED THEN UPDATE SET ciphertext=@ct, updated_at=@updated
                WHEN NOT MATCHED THEN INSERT (id, project_id, kind, ciphertext, created_at, updated_at)
                    VALUES (@id, @pid, @kind, @ct, @created, @updated);
                """;
            cmd.AddParam("@id", r[0]); cmd.AddParam("@pid", r[1]); cmd.AddParam("@kind", r[2]);
            cmd.AddParam("@ct", r[3]); cmd.AddParam("@created", r[4]); cmd.AddParam("@updated", r[5]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (secrets.Count > 0) report.Add($"project '{src.ProjectId}': secrets={secrets.Count} (ciphertext as-is)");
    }

    // --- memory keys (source: memory.db when present, else issues.db) ---

    private static async Task CopyMemoryAsync(
        ProjectSource src, IDbConnectionFactory target, List<string> report, CancellationToken ct)
    {
        var d = target.Dialect;
        var sourcePath = src.MemoryDbPath is not null && File.Exists(src.MemoryDbPath)
            ? src.MemoryDbPath
            : src.IssuesDbPath;
        var rows = await ReadSqlite(sourcePath,
            "SELECT ts, [key], body, ttl_days FROM memory", ct,
            rd => new object?[]
            {
                rd.GetString(0), rd.GetString(1), rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetInt32(3),
            });
        if (rows.Count == 0)
        {
            report.Add($"project '{src.ProjectId}': memory=0");
            return;
        }
        await using var conn = await target.OpenAsync(ct);
        foreach (var r in rows)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                MERGE {d.Table("memory")} WITH (HOLDLOCK) AS t
                USING (SELECT @key AS [key]) AS s ON t.[key] = s.[key]
                WHEN MATCHED THEN UPDATE SET ts=@ts, body=@body, ttl_days=@ttl
                WHEN NOT MATCHED THEN INSERT (ts, [key], body, ttl_days) VALUES (@ts, @key, @body, @ttl);
                """;
            cmd.AddParam("@ts", r[0]); cmd.AddParam("@key", r[1]);
            cmd.AddParam("@body", r[2]); cmd.AddParam("@ttl", r[3]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        report.Add($"project '{src.ProjectId}': memory keys={rows.Count} (from {Path.GetFileName(sourcePath)})");
    }

    // --- --include-open-work: non-terminal issues + deps + linked specs/sprints ---

    private static readonly string[] TerminalStates = ["Completed", "Closed"];

    private static async Task CopyOpenWorkAsync(
        ProjectSource src, IDbConnectionFactory target, List<string> report, CancellationToken ct)
    {
        var d = target.Dialect;
        var terminal = string.Join("','", TerminalStates);
        var issues = await ReadSqlite(src.IssuesDbPath, $"""
            SELECT id, short_id, type, title, description, status, priority, assignee,
                   created_at, updated_at, closed_at, metadata_json, parent_issue_id,
                   dispatch_checkpoint, checkpoint_at, recovery_attempts
            FROM issue WHERE status NOT IN ('{terminal}')
            """, ct,
            rd => new object?[]
            {
                rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4), rd.GetString(5), rd.GetInt32(6),
                rd.IsDBNull(7) ? null : rd.GetString(7), rd.GetString(8), rd.GetString(9),
                rd.IsDBNull(10) ? null : rd.GetString(10), rd.GetString(11),
                rd.IsDBNull(12) ? null : rd.GetString(12),
                rd.IsDBNull(13) ? null : rd.GetString(13),
                rd.IsDBNull(14) ? null : rd.GetString(14), rd.GetInt32(15),
            });
        if (issues.Count == 0)
        {
            report.Add($"project '{src.ProjectId}': open work=0");
            return;
        }
        var ids = issues.Select(r => (string)r[0]!).ToHashSet(StringComparer.Ordinal);

        await using var conn = await target.OpenAsync(ct);
        foreach (var r in issues)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                IF NOT EXISTS (SELECT 1 FROM {d.Table("issue")} WHERE id = @id)
                INSERT INTO {d.Table("issue")} (id, short_id, type, title, description, status, priority, assignee,
                    created_at, updated_at, closed_at, metadata_json, parent_issue_id,
                    dispatch_checkpoint, checkpoint_at, recovery_attempts)
                VALUES (@id, @short, @type, @title, @desc, @status, @pri, @assignee,
                    @created, @updated, @closed, @meta, @parent, @cp, @cpAt, @ra);
                """;
            cmd.AddParam("@id", r[0]); cmd.AddParam("@short", r[1]); cmd.AddParam("@type", r[2]);
            cmd.AddParam("@title", r[3]); cmd.AddParam("@desc", r[4]); cmd.AddParam("@status", r[5]);
            cmd.AddParam("@pri", r[6]); cmd.AddParam("@assignee", r[7]); cmd.AddParam("@created", r[8]);
            cmd.AddParam("@updated", r[9]); cmd.AddParam("@closed", r[10]); cmd.AddParam("@meta", r[11]);
            cmd.AddParam("@parent", r[12]); cmd.AddParam("@cp", r[13]); cmd.AddParam("@cpAt", r[14]);
            cmd.AddParam("@ra", r[15]);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // issue_dep edges where both ends were copied.
        var deps = await ReadSqlite(src.IssuesDbPath,
            "SELECT blocker_id, blocked_id, kind, created_at FROM issue_dep", ct,
            rd => new object?[] { rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3) });
        var depCount = 0;
        foreach (var r in deps)
        {
            if (!ids.Contains((string)r[0]!) || !ids.Contains((string)r[1]!)) continue;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                IF NOT EXISTS (SELECT 1 FROM {d.Table("issue_dep")} WHERE blocker_id=@b AND blocked_id=@k AND kind=@kind)
                INSERT INTO {d.Table("issue_dep")} (blocker_id, blocked_id, kind, created_at) VALUES (@b, @k, @kind, @created);
                """;
            cmd.AddParam("@b", r[0]); cmd.AddParam("@k", r[1]); cmd.AddParam("@kind", r[2]); cmd.AddParam("@created", r[3]);
            await cmd.ExecuteNonQueryAsync(ct);
            depCount++;
        }

        // issue_seq counters: seed above the highest copied short_id per
        // type so the first post-cutover task can't collide with a
        // copied id (issue.id is type-shortId).
        foreach (var group in issues.GroupBy(r => (string)r[2]!))
        {
            var max = group
                .Select(r => int.TryParse((string)r[1]!, out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                MERGE {d.Table("issue_seq")} WITH (HOLDLOCK) AS t
                USING (SELECT @t AS type) AS s ON t.type = s.type
                WHEN MATCHED AND t.next_short < @next THEN UPDATE SET next_short = @next
                WHEN NOT MATCHED THEN INSERT (type, next_short) VALUES (@t, @next);
                """;
            cmd.AddParam("@t", group.Key);
            cmd.AddParam("@next", max + 1);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Specs linked to copied issues (+ all their versions and
        // extraction rows — the body is the working document, not
        // history).
        var specIds = new List<string>();
        {
            var inList = string.Join(",", ids.Select((id, i) => $"@p{i}"));
            var specs = await ReadSqlite(src.IssuesDbPath,
                $"SELECT id FROM spec WHERE parent_issue_id IN ({inList})", ct,
                rd => rd.GetString(0),
                ids.Cast<object?>().ToArray());
            specIds.AddRange(specs);
        }
        foreach (var specId in specIds)
        {
            var head = await ReadSqlite(src.IssuesDbPath,
                "SELECT id, project_id, title, status, parent_issue_id, parent_spec_id, current_version, created_at, updated_at, extracted_at FROM spec WHERE id = @p0", ct,
                rd => new object?[]
                {
                    rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                    rd.GetInt32(6), rd.GetString(7), rd.GetString(8),
                    rd.IsDBNull(9) ? null : rd.GetString(9),
                },
                new object?[] { specId });
            foreach (var r in head)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    IF NOT EXISTS (SELECT 1 FROM {d.Table("spec")} WHERE id = @id)
                    INSERT INTO {d.Table("spec")} (id, project_id, title, status, parent_issue_id, parent_spec_id, current_version, created_at, updated_at, extracted_at)
                    VALUES (@id, @proj, @title, @status, @pIssue, @pSpec, @cv, @created, @updated, @extracted);
                    """;
                cmd.AddParam("@id", r[0]); cmd.AddParam("@proj", r[1]); cmd.AddParam("@title", r[2]);
                cmd.AddParam("@status", r[3]); cmd.AddParam("@pIssue", r[4]); cmd.AddParam("@pSpec", r[5]);
                cmd.AddParam("@cv", r[6]); cmd.AddParam("@created", r[7]); cmd.AddParam("@updated", r[8]);
                cmd.AddParam("@extracted", r[9]);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var versions = await ReadSqlite(src.IssuesDbPath,
                "SELECT spec_id, version, body, author, created_at FROM spec_version WHERE spec_id = @p0", ct,
                rd => new object?[]
                {
                    rd.GetString(0), rd.GetInt32(1), rd.GetString(2),
                    rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4),
                },
                new object?[] { specId });
            foreach (var r in versions)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    IF NOT EXISTS (SELECT 1 FROM {d.Table("spec_version")} WHERE spec_id = @sid AND version = @v)
                    INSERT INTO {d.Table("spec_version")} (spec_id, version, body, author, created_at)
                    VALUES (@sid, @v, @body, @author, @created);
                    """;
                cmd.AddParam("@sid", r[0]); cmd.AddParam("@v", r[1]); cmd.AddParam("@body", r[2]);
                cmd.AddParam("@author", r[3]); cmd.AddParam("@created", r[4]);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // Active sprints + their links to copied issues.
        var sprints = await ReadSqlite(src.IssuesDbPath,
            "SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM sprint WHERE status = 'Active'", ct,
            rd => new object?[]
            {
                rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
            });
        var linkCount = 0;
        foreach (var r in sprints)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                IF NOT EXISTS (SELECT 1 FROM {d.Table("sprint")} WHERE id = @id)
                INSERT INTO {d.Table("sprint")} (id, name, goal, start_date, end_date, status, created_at, updated_at)
                VALUES (@id, @name, @goal, @start, @end, @status, @created, @updated);
                """;
            cmd.AddParam("@id", r[0]); cmd.AddParam("@name", r[1]); cmd.AddParam("@goal", r[2]);
            cmd.AddParam("@start", r[3]); cmd.AddParam("@end", r[4]); cmd.AddParam("@status", r[5]);
            cmd.AddParam("@created", r[6]); cmd.AddParam("@updated", r[7]);
            await cmd.ExecuteNonQueryAsync(ct);

            var links = await ReadSqlite(src.IssuesDbPath,
                "SELECT issue_id, added_at FROM sprint_issue WHERE sprint_id = @p0", ct,
                rd => new object?[] { rd.GetString(0), rd.GetString(1) },
                new object?[] { r[0] });
            foreach (var l in links)
            {
                if (!ids.Contains((string)l[0]!)) continue;
                await using var lcmd = conn.CreateCommand();
                lcmd.CommandText = $"""
                    IF NOT EXISTS (SELECT 1 FROM {d.Table("sprint_issue")} WHERE sprint_id = @sid AND issue_id = @iid)
                    INSERT INTO {d.Table("sprint_issue")} (sprint_id, issue_id, added_at) VALUES (@sid, @iid, @added);
                    """;
                lcmd.AddParam("@sid", r[0]); lcmd.AddParam("@iid", l[0]); lcmd.AddParam("@added", l[1]);
                await lcmd.ExecuteNonQueryAsync(ct);
                linkCount++;
            }
        }

        report.Add($"project '{src.ProjectId}': open work issues={issues.Count} deps={depCount} specs={specIds.Count} activeSprints={sprints.Count} sprintLinks={linkCount}");
    }

    // --- SQLite source helpers ---

    private static bool SqliteTableExists(string dbPath, string table)
    {
        if (!File.Exists(dbPath)) return false;
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@t LIMIT 1";
        cmd.Parameters.AddWithValue("@t", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static async Task<List<T>> ReadSqlite<T>(
        string dbPath,
        string sql,
        CancellationToken ct,
        Func<SqliteDataReader, T> map,
        IReadOnlyList<object?>? parameters = null)
    {
        var list = new List<T>();
        if (!File.Exists(dbPath)) return list;
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (parameters is not null)
        {
            for (var i = 0; i < parameters.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", parameters[i] ?? DBNull.Value);
        }
        try
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(map(rd));
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            // Source DB predates the table (e.g. secret on an old file) — nothing to copy.
        }
        return list;
    }
}
