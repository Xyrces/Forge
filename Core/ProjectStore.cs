using Microsoft.Data.Sqlite;

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
    string? LastSyncError);

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
}

public sealed class ProjectStore : IProjectStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public ProjectStore(IssueStore issues) { _issues = issues; }

    public async Task<ProjectRecord> UpsertAsync(NewProject p, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Id))
            throw new InvalidOperationException("Project id is required.");
        if (string.IsNullOrWhiteSpace(p.RepoUrl))
            throw new InvalidOperationException($"Project '{p.Id}' has no RepoUrl.");

        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;

        // INSERT OR IGNORE first, then UPDATE — preserves original
        // created_at on existing rows; sets it fresh on new rows.
        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = (SqliteTransaction)tx;
            ins.CommandText = @"INSERT OR IGNORE INTO project
                (id, name, repo_url, default_branch, created_at, updated_at)
                VALUES ($id, $name, $url, $branch, $now, $now)";
            ins.Parameters.AddWithValue("$id", p.Id);
            ins.Parameters.AddWithValue("$name", p.Name);
            ins.Parameters.AddWithValue("$url", p.RepoUrl);
            ins.Parameters.AddWithValue("$branch", string.IsNullOrWhiteSpace(p.DefaultBranch) ? "main" : p.DefaultBranch);
            ins.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(now));
            await ins.ExecuteNonQueryAsync(ct);
        }

        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = (SqliteTransaction)tx;
            upd.CommandText = @"UPDATE project SET
                name = $name,
                repo_url = $url,
                default_branch = $branch,
                updated_at = $now
                WHERE id = $id";
            upd.Parameters.AddWithValue("$id", p.Id);
            upd.Parameters.AddWithValue("$name", p.Name);
            upd.Parameters.AddWithValue("$url", p.RepoUrl);
            upd.Parameters.AddWithValue("$branch", string.IsNullOrWhiteSpace(p.DefaultBranch) ? "main" : p.DefaultBranch);
            upd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(now));
            await upd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        var existing = await GetAsync(p.Id, ct)
            ?? throw new InvalidOperationException($"Project '{p.Id}' vanished after upsert.");
        return existing;
    }

    public async Task<ProjectRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, name, repo_url, default_branch, local_path, created_at, updated_at, last_synced_at, last_sync_error
            FROM project WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, name, repo_url, default_branch, local_path, created_at, updated_at, last_synced_at, last_sync_error
            FROM project ORDER BY id";
        var list = new List<ProjectRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM project WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task UpdateLocalPathAsync(string id, string localPath, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE project SET local_path = $path, updated_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$path", localPath);
        cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateSyncStatusAsync(string id, DateTime syncedAt, string? error, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE project SET
            last_synced_at = $when,
            last_sync_error = $err,
            updated_at = $now
            WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$when", IssueStore.DateFormatTime(syncedAt));
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ProjectRecord Read(SqliteDataReader rd) => new(
        Id: rd.GetString(0),
        Name: rd.GetString(1),
        RepoUrl: rd.GetString(2),
        DefaultBranch: rd.GetString(3),
        LocalPath: rd.IsDBNull(4) ? null : rd.GetString(4),
        CreatedAt: IssueStore.ParseTime(rd.GetString(5)),
        UpdatedAt: IssueStore.ParseTime(rd.GetString(6)),
        LastSyncedAt: rd.IsDBNull(7) ? null : IssueStore.ParseTime(rd.GetString(7)),
        LastSyncError: rd.IsDBNull(8) ? null : rd.GetString(8));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}
