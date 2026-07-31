using Forge.Core;
using Forge.Core.Db;

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
    Task ClearAsync(CancellationToken ct = default);
}

public sealed class CodebaseGraphCacheStore : ICodebaseGraphCacheStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public CodebaseGraphCacheStore(IssueStore issues) { _issues = issues; }

    private string T(string name) => _issues.Db.Dialect.Table(name);

    public async Task<CodebaseGraphCache?> GetAsync(string repoRoot, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT repo_sha, built_at, file_count, edge_count
                            FROM {T("codebase_graph_cache")}
                            WHERE repo_sha = (SELECT MAX(repo_sha) FROM {T("codebase_graph_cache")})";
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
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _issues.Db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                MERGE {T("codebase_graph_cache")} WITH (HOLDLOCK) AS t
                USING (SELECT @sha AS repo_sha) AS s ON t.repo_sha = s.repo_sha
                WHEN MATCHED THEN UPDATE SET built_at = @built, file_count = @files, edge_count = @edges
                WHEN NOT MATCHED THEN INSERT (repo_sha, built_at, file_count, edge_count) VALUES (@sha, @built, @files, @edges);
                """
            : $"""
                INSERT INTO {T("codebase_graph_cache")} (repo_sha, built_at, file_count, edge_count)
                VALUES (@sha, @built, @files, @edges)
                ON CONFLICT(repo_sha) DO UPDATE SET
                  built_at = excluded.built_at,
                  file_count = excluded.file_count,
                  edge_count = excluded.edge_count
                """;
        cmd.AddParam("@sha", entry.RepoSha);
        cmd.AddParam("@built", IssueStore.DateFormatTime(entry.BuiltAt));
        cmd.AddParam("@files", entry.FileCount);
        cmd.AddParam("@edges", entry.EdgeCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("codebase_graph_cache")}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}