using Microsoft.Data.Sqlite;
using Forge.Core;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Verifies that <see cref="SpecStore"/> populates the derived
/// tables (spec_diagram, spec_touches, spec_dep) atomically with
/// every body update.
/// </summary>
public class SpecStoreExtractionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;

    public SpecStoreExtractionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-spec-ext-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(v);
    }

    private async Task<List<(string Kind, string Source)>> DiagramRowsAsync(string specId)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync();
        var list = new List<(string, string)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, source FROM spec_diagram WHERE spec_id = $id ORDER BY ordinal";
        cmd.Parameters.AddWithValue("$id", specId);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add((rd.GetString(0), rd.GetString(1)));
        return list;
    }

    private async Task<List<(string ModuleId, string? Rationale)>> TouchRowsAsync(string specId)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync();
        var list = new List<(string, string?)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT module_id, rationale FROM spec_touches WHERE spec_id = $id ORDER BY module_id";
        cmd.Parameters.AddWithValue("$id", specId);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1)));
        return list;
    }

    private async Task<List<(string Kind, string ToSpecId, string? Rationale)>> DepRowsAsync(string specId)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync();
        var list = new List<(string, string, string?)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, to_spec_id, rationale FROM spec_dep WHERE from_spec_id = $id ORDER BY to_spec_id";
        cmd.Parameters.AddWithValue("$id", specId);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add((rd.GetString(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
        return list;
    }

    [Fact]
    public async Task CreateAsync_PopulatesDerivedTables()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P",
            Title: "Dark mode",
            Body: """
                ## Summary
                Add dark mode.

                ## Diagrams
                ```mermaid
                flowchart LR
                  A --> B
                ```

                ## Touches
                - PortHorizon.Dashboard.Theming

                ## Dependencies
                - blocks spec-portal-redirect
                """));

        var diagrams = await DiagramRowsAsync(spec.Id);
        Assert.Single(diagrams);
        Assert.Equal("flowchart", diagrams[0].Kind);

        var touches = await TouchRowsAsync(spec.Id);
        Assert.Single(touches);
        Assert.Equal("PortHorizon.Dashboard.Theming", touches[0].ModuleId);

        var deps = await DepRowsAsync(spec.Id);
        Assert.Single(deps);
        Assert.Equal("blocks", deps[0].Kind);
        Assert.Equal("spec-portal-redirect", deps[0].ToSpecId);

        // extracted_at should be populated.
        var extracted = await CountAsync(
            $"SELECT COUNT(*) FROM spec WHERE id = '{spec.Id}' AND extracted_at IS NOT NULL");
        Assert.Equal(1, extracted);
    }

    [Fact]
    public async Task UpdateBodyAsync_RewritesDerivedTables()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T",
            Body: """
                ## Touches
                - OldModule
                """));

        var before = await TouchRowsAsync(spec.Id);
        Assert.Single(before);
        Assert.Equal("OldModule", before[0].ModuleId);

        await _specs.UpdateBodyAsync(spec.Id, new UpdateSpecBody("""
            ## Touches
            - NewModule1
            - NewModule2

            ## Diagrams
            ```mermaid
            sequenceDiagram
              A->>B: x
            ```
            """, Author: "alice"));

        var after = await TouchRowsAsync(spec.Id);
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, t => t.ModuleId == "OldModule");
        Assert.Contains(after, t => t.ModuleId == "NewModule1");
        Assert.Contains(after, t => t.ModuleId == "NewModule2");

        var diagrams = await DiagramRowsAsync(spec.Id);
        Assert.Single(diagrams);
        Assert.Equal("sequencediagram", diagrams[0].Kind);
    }

    [Fact]
    public async Task UpdateBodyAsync_AddsNewTouchesWithoutDroppingOld()
    {
        // The agent appends "## Touches" sections on each call. The
        // extractor handles this by parsing the latest version, so
        // the resulting spec_touches table reflects the *latest* body,
        // not the union of all bodies. (Source-of-truth-is-body.)
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T", Body: "## Touches\n- ModuleA"));

        await _specs.UpdateBodyAsync(spec.Id, new UpdateSpecBody("## Touches\n- ModuleA\n- ModuleB", "alice"));

        var after = await TouchRowsAsync(spec.Id);
        Assert.Equal(2, after.Count);
        Assert.Contains(after, t => t.ModuleId == "ModuleA");
        Assert.Contains(after, t => t.ModuleId == "ModuleB");
    }

    [Fact]
    public async Task DepRows_IncludeRationale()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T",
            Body: """
                ## Dependencies
                - depends_on spec-other — shared auth module
                """));

        var deps = await DepRowsAsync(spec.Id);
        Assert.Single(deps);
        Assert.Equal("depends_on", deps[0].Kind);
        Assert.Equal("spec-other", deps[0].ToSpecId);
        Assert.Contains("shared auth module", deps[0].Rationale);
    }

    [Fact]
    public async Task BodyWithoutSections_LeavesDerivedTablesEmpty()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T", Body: "Just some prose, no headings."));

        var diagrams = await DiagramRowsAsync(spec.Id);
        var touches = await TouchRowsAsync(spec.Id);
        var deps = await DepRowsAsync(spec.Id);
        Assert.Empty(diagrams);
        Assert.Empty(touches);
        Assert.Empty(deps);
    }
}