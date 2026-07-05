using Microsoft.Data.Sqlite;
using Forge.Core;

namespace Forge.Codebase;

/// <summary>
/// SQLite-backed manifest for the codebase graph cache. Tracks the
/// last-known <c>RepoSha</c> + a pointer to the on-disk JSON file.
/// The actual graph JSON lives at
/// <c>.portHorizon/codebase-graph/&lt;sha&gt;.json</c>; the manifest
/// is just a "what we have cached" pointer so the next call can do
/// a no-op if the sha hasn't changed.
/// </summary>
public interface ICodebaseGraphCacheStore
{
    Task<CodebaseGraphCache?> GetAsync(string repoRoot, CancellationToken ct = default);
    Task UpsertAsync(CodebaseGraphCache entry, CancellationToken ct = default);
}

public sealed class CodebaseGraphCacheStore : ICodebaseGraphCacheStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public CodebaseGraphCacheStore(IssueStore issues) { _issues = issues; }

    public async Task<CodebaseGraphCache?> GetAsync(string repoRoot, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT repo_sha, built_at, file_count, edge_count
                            FROM codebase_graph_cache
                            WHERE repo_sha = (SELECT MAX(repo_sha) FROM codebase_graph_cache)";
        // We use MAX(repo_sha) as a stand-in for "most recent" since we
        // have one row per sha. For multi-repo we'd key by repoRoot;
        // not yet supported.
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var sha = rd.GetString(0);
        var builtAt = IssueStore.ParseTime(rd.GetString(1));
        var fileCount = rd.GetInt32(2);
        var edgeCount = rd.GetInt32(3);
        var diskPath = Path.Combine(repoRoot, ".portHorizon", "codebase-graph", sha + ".json");
        return new CodebaseGraphCache(builtAt, sha, fileCount, edgeCount, diskPath);
    }

    public async Task UpsertAsync(CodebaseGraphCache entry, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO codebase_graph_cache (repo_sha, built_at, file_count, edge_count)
                            VALUES ($sha, $built, $files, $edges)
                            ON CONFLICT(repo_sha) DO UPDATE SET
                              built_at = excluded.built_at,
                              file_count = excluded.file_count,
                              edge_count = excluded.edge_count";
        cmd.Parameters.AddWithValue("$sha", entry.RepoSha);
        cmd.Parameters.AddWithValue("$built", IssueStore.DateFormatTime(entry.BuiltAt));
        cmd.Parameters.AddWithValue("$files", entry.FileCount);
        cmd.Parameters.AddWithValue("$edges", entry.EdgeCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}