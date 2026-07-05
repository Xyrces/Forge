using Microsoft.Extensions.Logging.Abstractions;
using Forge.AgentTools;
using Forge.Core;
using Xunit;

namespace PortHorizon.Agents.Tests;

/// <summary>
/// P5.1 — Native SharedContext equivalent. Tests for
/// ArtifactReadTool (read by id from any of design/spec/art)
/// + ContextHandoffStore (lineage recording).
/// </summary>
public class ArtifactReadToolTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly DesignArtifactStore _designArtifacts;
    private readonly ArtOutputStore _artOutputs;
    private readonly SpecStore _specs;
    private readonly ContextHandoffStore _handoffs;

    public ArtifactReadToolTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-artread-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _designArtifacts = new DesignArtifactStore(Path.Combine(_workDir, "issues.db"));
        _artOutputs = new ArtOutputStore(Path.Combine(_workDir, "issues.db"));
        _specs = new SpecStore(_issues);
        _handoffs = new ContextHandoffStore(Path.Combine(_workDir, "issues.db"));
        // The IssueStore ctor already ran the v12 migration
        // (which creates the context_handoff table). We don't
        // need to call EnsureCreatedAsync again; the table is
        // already there.
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private ArtifactReadTool NewTool() => new(_designArtifacts, _specs, _artOutputs, _handoffs, NullLogger<ArtifactReadTool>.Instance);

    [Fact]
    public async Task ReadArtifact_DesignId_ReturnsBody()
    {
        var design = await _designArtifacts.CreateAsync(new NewDesignArtifact(
            "spec-1", DesignArtifactKind.Wireframe, "Crate", "mesh body", "html"));
        var tool = NewTool();
        var result = await tool.ReadArtifactAsync(design.Id);
        Assert.Contains("\"id\":", result);
        Assert.Contains("\"kind\":\"design\"", result);
        Assert.Contains("mesh body", result);
    }

    [Fact]
    public async Task ReadArtifact_SpecId_ReturnsBody()
    {
        var spec = await _specs.CreateAsync(new NewSpec("P", "Title", "spec body content"));
        var tool = NewTool();
        var result = await tool.ReadArtifactAsync(spec.Id);
        Assert.Contains("\"kind\":\"spec\"", result);
        Assert.Contains("spec body content", result);
    }

    [Fact]
    public async Task ReadArtifact_ArtId_ReturnsRelativePath()
    {
        var art = await _artOutputs.CreateAsync(new NewArtOutput(
            "spec-1", ArtOutputKind.Mesh, "Crate", "spec-1/crate.glb", "glb"));
        var tool = NewTool();
        var result = await tool.ReadArtifactAsync(art.Id);
        Assert.Contains("\"kind\":\"art\"", result);
        Assert.Contains("spec-1/crate.glb", result);
    }

    [Fact]
    public async Task ReadArtifact_NotFound_ReturnsErrorEnvelope()
    {
        var tool = NewTool();
        var result = await tool.ReadArtifactAsync("design-does-not-exist");
        Assert.Contains("\"error\":\"not_found\"", result);
    }

    [Fact]
    public async Task ReadArtifact_EmptyId_ReturnsErrorEnvelope()
    {
        var tool = NewTool();
        var result = await tool.ReadArtifactAsync("");
        Assert.Contains("\"error\":\"empty_id\"", result);
    }

    [Fact]
    public async Task ReadArtifact_UnknownPrefix_ReturnsErrorEnvelope()
    {
        var tool = NewTool();
        // Id with no recognized prefix.
        var result = await tool.ReadArtifactAsync("wat-foo");
        Assert.Contains("\"error\":\"not_found\"", result);
    }

    [Fact]
    public async Task ReadArtifact_LogsToContextHandoff()
    {
        var spec = await _specs.CreateAsync(new NewSpec("P", "Title", "log me"));
        var tool = NewTool();
        await tool.ReadArtifactAsync(spec.Id);
        // Use a placeholder taskId for now; the tool doesn't
        // currently receive taskId. The handoff log uses
        // empty string for taskId. After the P5.1 wiring pass
        // this would be the actual taskId from the runner
        // context.
        var entries = await _handoffs.ListForTaskAsync("");
        Assert.Contains(entries, e => e.ArtifactId == spec.Id && e.ArtifactKind == "spec" && e.Consumed);
    }

    [Fact]
    public async Task ContextHandoff_RoundTrip_PreservesFields()
    {
        await _handoffs.LogReadAsync(
            artifactId: "design-abc", kind: "design",
            taskId: "task-1", fromRole: "designer", toRole: "artist",
            consumed: true);
        var entries = await _handoffs.ListForTaskAsync("task-1");
        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("task-1", e.TaskId);
        Assert.Equal("design-abc", e.ArtifactId);
        Assert.Equal("design", e.ArtifactKind);
        Assert.Equal("designer", e.FromRole);
        Assert.Equal("artist", e.ToRole);
        Assert.True(e.Consumed);
    }

    [Fact]
    public async Task ContextHandoff_MissedRead_StoredWithConsumedFalse()
    {
        // The P5.1 tool records consumed=true on a hit and
        // consumed=false on a miss. The latter is useful for
        // dashboard signals like 'agent asked for an artifact
        // that doesn't exist' — usually a stale id.
        await _handoffs.LogReadAsync(
            artifactId: "design-missing", kind: "design",
            taskId: "task-1", fromRole: "designer", toRole: "artist",
            consumed: false);
        var entries = await _handoffs.ListForTaskAsync("task-1");
        Assert.Single(entries);
        Assert.False(entries[0].Consumed);
    }

    [Fact]
    public async Task AsAIFunction_ReturnsCallDescriptor()
    {
        var tool = NewTool();
        var fn = tool.AsAIFunction();
        Assert.NotNull(fn);
        Assert.Equal("read_artifact", fn.Name);
    }
}
