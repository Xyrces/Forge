using Microsoft.Data.Sqlite;

namespace PortHorizon.Agents.Core;

/// <summary>
/// One row of <c>spec_diagram</c>, in extraction order. Returned
/// to the dashboard so the Spec side-panel can render each Mermaid
/// block inline.
/// </summary>
public sealed record SpecDiagramRow(
    string SpecId,
    int Ordinal,
    string Kind,
    string Source,
    string? Title);

/// <summary>
/// One row of <c>spec_touches</c>. <c>Source</c> is "auto" (from
/// the body extractor) or "declared" (from a future
/// touch()/AIFunction call).
/// </summary>
public sealed record SpecTouchRow(
    string SpecId,
    string ModuleId,
    string Source,
    string? Rationale,
    DateTime CreatedAt);

/// <summary>
/// One row of <c>spec_dep</c>. The target spec may not exist in
/// the catalog yet (forward references); the dashboard renders
/// the target id verbatim and links to it if it does exist.
/// </summary>
public sealed record SpecDepRow(
    string FromSpecId,
    string ToSpecId,
    string Kind,
    string? Rationale,
    string Source,
    DateTime CreatedAt);

/// <summary>
/// Read access to the spec extraction tables. The Phase 2a
/// SpecBodyExtractor writes these tables; the dashboard's side
/// panel (Phase 2b) reads them. Separated from ISpecStore so
/// test doubles / NullSpecStore only need to stub what's relevant
/// for their concern.
/// </summary>
public interface ISpecExtractionReader
{
    Task<IReadOnlyList<SpecDiagramRow>> GetDiagramsAsync(string specId, CancellationToken ct = default);
    Task<IReadOnlyList<SpecTouchRow>> GetTouchesAsync(string specId, CancellationToken ct = default);
    Task<IReadOnlyList<SpecDepRow>> GetDepsAsync(string specId, CancellationToken ct = default);
    Task<IReadOnlyList<SpecRecord>> ListByParentIssueIdAsync(string parentIssueId, CancellationToken ct = default);
}

public sealed class SpecExtractionReader : ISpecExtractionReader
{
    private readonly IssueStore _issues;
    public SpecExtractionReader(IssueStore issues) { _issues = issues; }

    public async Task<IReadOnlyList<SpecDiagramRow>> GetDiagramsAsync(string specId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        var list = new List<SpecDiagramRow>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT spec_id, ordinal, kind, source, title
                            FROM spec_diagram WHERE spec_id = $id ORDER BY ordinal";
        cmd.Parameters.AddWithValue("$id", specId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new SpecDiagramRow(
                SpecId: rd.GetString(0),
                Ordinal: rd.GetInt32(1),
                Kind: rd.GetString(2),
                Source: rd.GetString(3),
                Title: rd.IsDBNull(4) ? null : rd.GetString(4)));
        }
        return list;
    }

    public async Task<IReadOnlyList<SpecTouchRow>> GetTouchesAsync(string specId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        var list = new List<SpecTouchRow>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT spec_id, module_id, source, rationale, created_at
                            FROM spec_touches WHERE spec_id = $id ORDER BY module_id";
        cmd.Parameters.AddWithValue("$id", specId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new SpecTouchRow(
                SpecId: rd.GetString(0),
                ModuleId: rd.GetString(1),
                Source: rd.GetString(2),
                Rationale: rd.IsDBNull(3) ? null : rd.GetString(3),
                CreatedAt: IssueStore.ParseTime(rd.GetString(4))));
        }
        return list;
    }

    public async Task<IReadOnlyList<SpecDepRow>> GetDepsAsync(string specId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        var list = new List<SpecDepRow>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT from_spec_id, to_spec_id, kind, rationale, source, created_at
                            FROM spec_dep WHERE from_spec_id = $id ORDER BY to_spec_id, kind";
        cmd.Parameters.AddWithValue("$id", specId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new SpecDepRow(
                FromSpecId: rd.GetString(0),
                ToSpecId: rd.GetString(1),
                Kind: rd.GetString(2),
                Rationale: rd.IsDBNull(3) ? null : rd.GetString(3),
                Source: rd.GetString(4),
                CreatedAt: IssueStore.ParseTime(rd.GetString(5))));
        }
        return list;
    }

    public async Task<IReadOnlyList<SpecRecord>> ListByParentIssueIdAsync(string parentIssueId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        var list = new List<SpecRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT s.id, s.project_id, s.title, s.status, s.parent_issue_id, s.parent_spec_id,
                s.current_version, s.created_at, s.updated_at,
                v.body, v.author
                FROM spec s
                JOIN spec_version v ON v.spec_id = s.id AND v.version = s.current_version
                WHERE s.parent_issue_id = $pid
                ORDER BY s.created_at";
        cmd.Parameters.AddWithValue("$pid", parentIssueId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new SpecRecord(
                Id: rd.GetString(0),
                ProjectId: rd.GetString(1),
                Title: rd.GetString(2),
                Status: Enum.Parse<SpecStatus>(rd.GetString(3)),
                ParentIssueId: rd.IsDBNull(4) ? null : rd.GetString(4),
                ParentSpecId: rd.IsDBNull(5) ? null : rd.GetString(5),
                CurrentVersion: rd.GetInt32(6),
                CreatedAt: IssueStore.ParseTime(rd.GetString(7)),
                UpdatedAt: IssueStore.ParseTime(rd.GetString(8)),
                Body: rd.GetString(9),
                Author: rd.IsDBNull(10) ? null : rd.GetString(10)));
        }
        return list;
    }
}

/// <summary>
/// No-op read access used when the dashboard is run without a real
/// SpecExtractionReader. All read methods return empty.
/// </summary>
public sealed class NullSpecExtractionReader : ISpecExtractionReader
{
    public Task<IReadOnlyList<SpecDiagramRow>> GetDiagramsAsync(string specId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SpecDiagramRow>>(Array.Empty<SpecDiagramRow>());
    public Task<IReadOnlyList<SpecTouchRow>> GetTouchesAsync(string specId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SpecTouchRow>>(Array.Empty<SpecTouchRow>());
    public Task<IReadOnlyList<SpecDepRow>> GetDepsAsync(string specId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SpecDepRow>>(Array.Empty<SpecDepRow>());
    public Task<IReadOnlyList<SpecRecord>> ListByParentIssueIdAsync(string parentIssueId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SpecRecord>>(Array.Empty<SpecRecord>());
}