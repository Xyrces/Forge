using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core;

/// <summary>
/// One row per registered project. Backed by the <c>project</c> table
/// introduced in schema v17. The appsettings.json <c>projects[]</c>
/// list seeds the initial set on first boot (one-time copy); operators
/// can add/remove projects at runtime via <c>POST /api/projects</c> /
/// <c>DELETE /api/projects/{id}</c> thereafter.
/// </summary>
public sealed record ProjectRecord(
    string Id,
    string Name,
    string RepoUrl,
    string DefaultBranch,
    string? LocalPath,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastSyncedAt,
    string? LastSyncError,
    IReadOnlyDictionary<string, int>? Roles = null,
    IReadOnlyDictionary<string, RoleTerritory>? Territories = null)
{
    /// <summary>Per-project role-cap overrides (role -&gt; max). Empty = use defaults.</summary>
    public IReadOnlyDictionary<string, int> Roles { get; init; } =
        Roles ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-project role-territory overrides. Empty = built-in registry territory.</summary>
    public IReadOnlyDictionary<string, RoleTerritory> Territories { get; init; } =
        Territories ?? new Dictionary<string, RoleTerritory>(StringComparer.OrdinalIgnoreCase);
}

public sealed record NewProject(
    string Id,
    string Name,
    string RepoUrl,
    string DefaultBranch = "main");

public interface IProjectStore
{
    Task<ProjectRecord> UpsertAsync(NewProject project, CancellationToken ct = default);
    Task<ProjectRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectRecord>> ListAsync(CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task UpdateLocalPathAsync(string id, string localPath, CancellationToken ct = default);
    Task UpdateSyncStatusAsync(string id, DateTime syncedAt, string? error, CancellationToken ct = default);

    /// <summary>
    /// Replace the per-project role-cap overrides (role -&gt; max).
    /// Persisted to <c>project.roles_json</c>; the orchestrator seeds
    /// <c>SlotTable</c> from these on startup and the dashboard
    /// re-applies them live on save.
    /// </summary>
    Task<bool> UpdateRolesAsync(string id, IReadOnlyDictionary<string, int> roles, CancellationToken ct = default);

    /// <summary>
    /// Replace the per-project role-territory overrides (role -&gt;
    /// prefixes + root-file allowance), persisted under the reserved
    /// <c>$territory</c> key in <c>project.roles_json</c>. Caps are
    /// preserved. The agent runner re-reads the store per run, so edits
    /// take effect without a restart.
    /// </summary>
    Task<bool> UpdateTerritoriesAsync(string id, IReadOnlyDictionary<string, RoleTerritory> territories, CancellationToken ct = default);
}

public sealed class ProjectStore : IProjectStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public ProjectStore(IssueStore issues) { _issues = issues; }

    private string T(string name) => _issues.Db.Dialect.Table(name);

    public async Task<ProjectRecord> UpsertAsync(NewProject p, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Id))
            throw new InvalidOperationException("Project id is required.");
        if (string.IsNullOrWhiteSpace(p.RepoUrl))
            throw new InvalidOperationException($"Project '{p.Id}' has no RepoUrl.");

        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;

        // INSERT OR IGNORE first, then UPDATE — preserves original
        // created_at on existing rows; sets it fresh on new rows.
        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = _issues.Db.Provider == ForgeDbProvider.SqlServer
                ? $"""
                    IF NOT EXISTS (SELECT 1 FROM {T("project")} WHERE id = @id)
                    INSERT INTO {T("project")} (id, name, repo_url, default_branch, created_at, updated_at)
                    VALUES (@id, @name, @url, @branch, @now, @now);
                    """
                : """
                    INSERT OR IGNORE INTO project
                    (id, name, repo_url, default_branch, created_at, updated_at)
                    VALUES (@id, @name, @url, @branch, @now, @now)
                    """;
            ins.AddParam("@id", p.Id);
            ins.AddParam("@name", p.Name);
            ins.AddParam("@url", p.RepoUrl);
            ins.AddParam("@branch", string.IsNullOrWhiteSpace(p.DefaultBranch) ? "main" : p.DefaultBranch);
            ins.AddParam("@now", IssueStore.DateFormatTime(now));
            await ins.ExecuteNonQueryAsync(ct);
        }

        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = $"""
                UPDATE {T("project")} SET
                name = @name,
                repo_url = @url,
                default_branch = @branch,
                updated_at = @now
                WHERE id = @id
                """;
            upd.AddParam("@id", p.Id);
            upd.AddParam("@name", p.Name);
            upd.AddParam("@url", p.RepoUrl);
            upd.AddParam("@branch", string.IsNullOrWhiteSpace(p.DefaultBranch) ? "main" : p.DefaultBranch);
            upd.AddParam("@now", IssueStore.DateFormatTime(now));
            await upd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        var existing = await GetAsync(p.Id, ct)
            ?? throw new InvalidOperationException($"Project '{p.Id}' vanished after upsert.");
        return existing;
    }

    public async Task<ProjectRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, repo_url, default_branch, local_path, created_at, updated_at, last_synced_at, last_sync_error, roles_json
            FROM {T("project")} WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, repo_url, default_branch, local_path, created_at, updated_at, last_synced_at, last_sync_error, roles_json
            FROM {T("project")} ORDER BY id
            """;
        var list = new List<ProjectRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("project")} WHERE id = @id";
        cmd.AddParam("@id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task UpdateLocalPathAsync(string id, string localPath, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""UPDATE {T("project")} SET local_path = @path, updated_at = @now WHERE id = @id""";
        cmd.AddParam("@id", id);
        cmd.AddParam("@path", localPath);
        cmd.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateSyncStatusAsync(string id, DateTime syncedAt, string? error, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {T("project")} SET
            last_synced_at = @when,
            last_sync_error = @err,
            updated_at = @now
            WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        cmd.AddParam("@when", IssueStore.DateFormatTime(syncedAt));
        cmd.AddParam("@err", (object?)error ?? DBNull.Value);
        cmd.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateRolesAsync(string id, IReadOnlyDictionary<string, int> roles, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        await using var conn = await _issues.Db.OpenAsync(ct);
        var existing = await ReadRolesJsonAsync(conn, id, ct);
        var json = SerializeRolesJson(roles, existing.Territories);
        return await WriteRolesJsonAsync(conn, id, json, ct);
    }

    public async Task<bool> UpdateTerritoriesAsync(string id, IReadOnlyDictionary<string, RoleTerritory> territories, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(territories);
        await using var conn = await _issues.Db.OpenAsync(ct);
        var existing = await ReadRolesJsonAsync(conn, id, ct);
        if (!existing.Exists) return false;
        var json = SerializeRolesJson(existing.Roles, territories);
        return await WriteRolesJsonAsync(conn, id, json, ct);
    }

    private async Task<(bool Exists, IReadOnlyDictionary<string, int> Roles, IReadOnlyDictionary<string, RoleTerritory> Territories)> ReadRolesJsonAsync(
        System.Data.Common.DbConnection conn, string id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""SELECT roles_json FROM {T("project")} WHERE id = @id""";
        cmd.AddParam("@id", id);
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (raw is null || raw is DBNull) return (false, new Dictionary<string, int>(), new Dictionary<string, RoleTerritory>());
        var (roles, territories) = ParseRolesJson(raw as string);
        return (true, roles, territories);
    }

    private async Task<bool> WriteRolesJsonAsync(System.Data.Common.DbConnection conn, string id, string json, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""UPDATE {T("project")} SET roles_json = @roles, updated_at = @now WHERE id = @id""";
        cmd.AddParam("@id", id);
        cmd.AddParam("@roles", json);
        cmd.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static ProjectRecord Read(DbDataReader rd) => new(
        Id: rd.GetString(0),
        Name: rd.GetString(1),
        RepoUrl: rd.GetString(2),
        DefaultBranch: rd.GetString(3),
        LocalPath: rd.IsDBNull(4) ? null : rd.GetString(4),
        CreatedAt: IssueStore.ParseTime(rd.GetString(5)),
        UpdatedAt: IssueStore.ParseTime(rd.GetString(6)),
        LastSyncedAt: rd.IsDBNull(7) ? null : IssueStore.ParseTime(rd.GetString(7)),
        LastSyncError: rd.IsDBNull(8) ? null : rd.GetString(8),
        Roles: ParseRoles(rd.IsDBNull(9) ? null : rd.GetString(9)),
        Territories: ParseTerritories(rd.IsDBNull(9) ? null : rd.GetString(9)));

    /// <summary>Reserved roles_json key holding the territory block; keys
    /// starting with '$' are metadata, never role caps.</summary>
    internal const string TerritoryKey = "$territory";

    private static (IReadOnlyDictionary<string, int> Roles, IReadOnlyDictionary<string, RoleTerritory> Territories) ParseRolesJson(string? json)
        => (ParseRoles(json), ParseTerritories(json));

    private static System.Text.Json.Nodes.JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            // Corrupt roles_json shouldn't take the registry down;
            // treat as "no overrides" — the operator re-saves from the UI.
            return null;
        }
    }

    private static IReadOnlyDictionary<string, int> ParseRoles(string? json)
    {
        var obj = ParseObject(json);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (obj is null) return result;
        foreach (var kv in obj)
        {
            if (kv.Key.StartsWith('$')) continue;
            if (kv.Value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var max))
                result[kv.Key] = max;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, RoleTerritory> ParseTerritories(string? json)
    {
        var obj = ParseObject(json);
        var result = new Dictionary<string, RoleTerritory>(StringComparer.OrdinalIgnoreCase);
        if (obj is null
            || !obj.TryGetPropertyValue(TerritoryKey, out var block)
            || block is not System.Text.Json.Nodes.JsonObject terrObj)
            return result;
        foreach (var kv in terrObj)
        {
            if (kv.Value is not System.Text.Json.Nodes.JsonObject entry) continue;
            var prefixes = new List<string>();
            if (entry.TryGetPropertyValue("prefixes", out var p) && p is System.Text.Json.Nodes.JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var s = item?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(s)) prefixes.Add(s);
                }
            }
            var rootFiles = entry.TryGetPropertyValue("rootFiles", out var rf)
                && rf is System.Text.Json.Nodes.JsonValue rfv
                && rfv.TryGetValue<bool>(out var b) && b;
            result[kv.Key] = new RoleTerritory(prefixes, rootFiles);
        }
        return result;
    }

    private static string SerializeRolesJson(
        IReadOnlyDictionary<string, int> roles,
        IReadOnlyDictionary<string, RoleTerritory> territories)
    {
        var obj = new System.Text.Json.Nodes.JsonObject();
        foreach (var kv in roles)
        {
            if (kv.Key.StartsWith('$')) continue;
            obj[kv.Key] = kv.Value;
        }
        if (territories.Count > 0)
        {
            var block = new System.Text.Json.Nodes.JsonObject();
            foreach (var kv in territories)
            {
                block[kv.Key] = new System.Text.Json.Nodes.JsonObject
                {
                    ["prefixes"] = new System.Text.Json.Nodes.JsonArray(
                        kv.Value.Prefixes.Select(p => System.Text.Json.Nodes.JsonValue.Create(p)).ToArray()),
                    ["rootFiles"] = kv.Value.AllowsRootFiles,
                };
            }
            obj[TerritoryKey] = block;
        }
        return obj.ToJsonString();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}
