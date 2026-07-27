using Forge.Core;
using Forge.Core.Workflow;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Pass 1 guards: the built-in default definition reproduces the
/// previously hardcoded FlowGraph exactly (nodes, edges, layout),
/// and the resolver honors / falls back correctly.
/// </summary>
public sealed class WorkflowDefinitionTests : IDisposable
{
    private readonly string _workDir;

    public WorkflowDefinitionTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "forge-wf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private MemoryStore NewMemory()
    {
        var path = Path.Combine(_workDir, Guid.NewGuid().ToString("N") + ".db");
        var bootstrap = new IssueStore(path);   // owns the schema (incl. memory table)
        bootstrap.Dispose();
        return new MemoryStore(path);
    }

    [Fact]
    public void DefaultDefinition_MatchesThePreviouslyHardcodedGraph()
    {
        var d = WorkflowDefaults.Definition;

        Assert.Equal(
            new[] { "intake", "design", "groom", "backlog", "sprint",
                    "setup", "agent", "pr", "review", "done",
                    "rework", "parked", "blocked" },
            d.Steps.Select(s => s.Id).ToArray());

        Assert.Equal(
            new[] { "intake>design", "intake>groom", "design>groom", "groom>backlog",
                    "backlog>sprint", "sprint>setup", "setup>agent", "agent>pr",
                    "agent>done", "agent>blocked", "pr>review", "pr>rework",
                    "pr>parked", "parked>rework", "review>done", "review>rework",
                    "rework>agent", "rework>blocked" },
            d.Edges.Select(e => $"{e.From}>{e.To}").ToArray());

        // Layout: the Live-view SVG coordinates from the old statics.
        var byId = d.Steps.ToDictionary(s => s.Id);
        Assert.Equal((60, 90), (byId["intake"].X, byId["intake"].Y));
        Assert.Equal((660, 90), (byId["sprint"].X, byId["sprint"].Y));
        Assert.Equal((660, 290), (byId["setup"].X, byId["setup"].Y));
        Assert.Equal((60, 290), (byId["done"].X, byId["done"].Y));
        Assert.Equal((360, 420), (byId["rework"].X, byId["rework"].Y));
        Assert.Equal((60, 420), (byId["blocked"].X, byId["blocked"].Y));

        // Lanes.
        foreach (var id in new[] { "intake", "design", "groom", "backlog", "sprint" })
        {
            Assert.Equal(WorkflowLanes.Planning, byId[id].Lane);
        }
        foreach (var id in new[] { "setup", "agent", "pr", "review", "done", "rework", "parked", "blocked" })
        {
            Assert.Equal(WorkflowLanes.Implementation, byId[id].Lane);
        }
    }

    [Fact]
    public void DefaultDefinition_PoliciesMatchThePreviouslyHardcodedConstants()
    {
        var p = WorkflowDefaults.Definition.Policies;
        Assert.Equal("3", p[WorkflowPolicies.MaxStrikes]);                    // TaskStateProjector.MaxStrikes
        Assert.Equal("35", p[WorkflowPolicies.StallGraceMinutes]);            // TaskStateProjector.StallGrace
        Assert.Equal("true", p[WorkflowPolicies.ParkOnInfra]);
        Assert.Equal("true", p[WorkflowPolicies.AutoMerge]);
        Assert.Equal("completed", p[WorkflowPolicies.NoDiffOutcome]);
    }

    [Fact]
    public void DefaultDefinition_GatePlacementMatchesStageGates()
    {
        var byId = WorkflowDefaults.Definition.Steps.ToDictionary(s => s.Id);
        Assert.Equal(new[] { StageGates.Design }, byId["design"].Gates);
        Assert.Equal(new[] { StageGates.Groom }, byId["groom"].Gates);
        Assert.Equal(new[] { StageGates.Sprint }, byId["sprint"].Gates);
        Assert.Equal(new[] { StageGates.Merge }, byId["review"].Gates);
        Assert.Empty(byId["intake"].Gates);
        Assert.Empty(byId["agent"].Gates);
    }

    [Fact]
    public async Task Resolver_NoOverride_ReturnsDefault()
    {
        var memory = NewMemory();
        var resolved = await new WorkflowResolver(memory).ResolveAsync();
        Assert.Same(WorkflowDefaults.Definition, resolved);
    }

    [Fact]
    public async Task Resolver_NullMemory_ReturnsDefault()
    {
        var resolved = await new WorkflowResolver(memory: null).ResolveAsync();
        Assert.Same(WorkflowDefaults.Definition, resolved);
    }

    [Fact]
    public async Task Resolver_ValidOverride_Wins()
    {
        var memory = NewMemory();
        var custom = WorkflowDefaults.Definition with
        {
            Policies = new Dictionary<string, string>(WorkflowDefaults.Definition.Policies)
            {
                [WorkflowPolicies.MaxStrikes] = "5",
            },
        };
        await memory.RememberAsync(WorkflowResolver.LiveKey, WorkflowResolver.Serialize(custom));

        var resolved = await new WorkflowResolver(memory).ResolveAsync();
        Assert.Equal("5", resolved.Policies[WorkflowPolicies.MaxStrikes]);
        Assert.Equal(WorkflowDefaults.Definition.Steps.Count, resolved.Steps.Count);
    }

    [Fact]
    public async Task Resolver_CorruptOverride_FallsBackToDefault()
    {
        var memory = NewMemory();
        await memory.RememberAsync(WorkflowResolver.LiveKey, "{not json");
        var resolved = await new WorkflowResolver(memory).ResolveAsync();
        Assert.Same(WorkflowDefaults.Definition, resolved);
    }

    [Fact]
    public void RoundTrip_SerializeParse_PreservesDefinition()
    {
        var body = WorkflowResolver.Serialize(WorkflowDefaults.Definition);
        var parsed = WorkflowResolver.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal(WorkflowDefaults.Definition.Steps.Select(s => s.Id),
            parsed!.Steps.Select(s => s.Id));
        Assert.Equal(WorkflowDefaults.Definition.Edges.Select(e => (e.From, e.To, e.Kind)),
            parsed.Edges.Select(e => (e.From, e.To, e.Kind)));
        Assert.Equal(WorkflowDefaults.Definition.Policies, parsed.Policies);
    }

    [Fact]
    public void TryParse_EmptySteps_ReturnsNull()
    {
        var d = WorkflowDefaults.Definition with { Steps = Array.Empty<WorkflowStep>() };
        Assert.Null(WorkflowResolver.TryParse(WorkflowResolver.Serialize(d)));
    }
}
