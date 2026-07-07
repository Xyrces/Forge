using Forge.Configuration;
using Forge.Orchestrator.Slots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class ProjectRegistryLoaderTests
{
    [Fact]
    public void Load_WithLegacyWorkspaceRoot_ShimsSingleDefaultProject()
    {
        var options = new AgentOptions
        {
            Workspace = new WorkspaceOptions { Root = @"C:\legacy\root" },
        };
        var projects = ProjectRegistryLoader.Load(options, NullLogger.Instance);
        Assert.Single(projects);
        Assert.Equal("default", projects[0].Id);
        Assert.Equal(@"C:\legacy\root", projects[0].Root);
    }

    [Fact]
    public void Load_WithProjectsArray_TakesProjectsVerbatim()
    {
        var options = new AgentOptions
        {
            Workspace = new WorkspaceOptions { Root = @"C:\legacy\root" },
            Projects = new ProjectsOptions
            {
                Projects = new List<ProjectOptions>
                {
                    new() { Id = "porthorizon", Name = "PortHorizon", Root = @"C:\ph" },
                    new() { Id = "suikoden", Name = "Suikoden", Root = @"C:\sdk", Roles = new() { ["coredev"] = 3 } },
                },
            },
        };
        var projects = ProjectRegistryLoader.Load(options, NullLogger.Instance);
        Assert.Equal(2, projects.Count);
        Assert.DoesNotContain(projects, p => p.Id == "default");
        Assert.Equal(3, projects.Single(p => p.Id == "suikoden").Roles["coredev"]);
    }

    [Fact]
    public void Load_WithEmptyProjectsAndEmptyWorkspace_SynthesizesAutoScaffoldDefault()
    {
        var options = new AgentOptions();
        var projects = ProjectRegistryLoader.Load(options, NullLogger.Instance);
        var single = Assert.Single(projects);
        Assert.Equal("default", single.Id);
        Assert.Equal(string.Empty, single.Root);
    }

    [Fact]
    public void EnvOverride_TakesPrecedenceOverLegacyRoot()
    {
        var options = new AgentOptions { Workspace = new WorkspaceOptions { Root = @"C:\from-config" } };
        var env = new Dictionary<string, string> { ["FORGE_DEFAULT_PROJECT_ROOT"] = @"C:\from-env" };
        var projects = ProjectRegistryLoader.Load(options, NullLogger.Instance, env);
        Assert.Single(projects);
        Assert.Equal(@"C:\from-env", projects[0].Root);
    }
}

public class ProjectStateDirsTests
{
    // Expected paths are built with Path.Combine, not hardcoded '\'
    // literals -- ProjectStateDirs itself is cross-platform (it uses
    // Path.Combine throughout), so the test has to be too, or it fails
    // on Linux CI runners where Path.Combine joins with '/'.
    [Fact]
    public void Default_UsesLegacyFlatLayout()
    {
        var root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "ph");
        var p = new ProjectOptions { Id = "default", Root = root };
        Assert.Equal(Path.Combine(root, ".portHorizon", "state"), ProjectStateDirs.StateDirFor(p));
        Assert.Equal(Path.Combine(root, ".portHorizon", "state", "issues.db"), ProjectStateDirs.IssuesDbFor(p));
    }

    [Fact]
    public void NonDefault_GetPerProjectSubdir()
    {
        var root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "sdk");
        var p = new ProjectOptions { Id = "suikoden", Root = root };
        Assert.Equal(Path.Combine(root, ".portHorizon", "state", "suikoden"), ProjectStateDirs.StateDirFor(p));
        Assert.Equal(Path.Combine(root, ".portHorizon", "state", "suikoden", "memory.db"), ProjectStateDirs.MemoryDbFor(p));
    }

    [Fact]
    public void EmptyRoot_Throws()
    {
        var p = new ProjectOptions { Id = "x" };
        Assert.Throws<InvalidOperationException>(() => ProjectStateDirs.StateDirFor(p));
    }
}

public class SlotTableTests
{
    [Fact]
    public async Task Configure_ThenAcquire_HoldsCapacity()
    {
        var t = new SlotTable();
        t.Configure("p1", "coredev", 2);
        var a = await t.TryAcquireAsync("p1", "coredev", TimeSpan.FromMilliseconds(50), default);
        var b = await t.TryAcquireAsync("p1", "coredev", TimeSpan.FromMilliseconds(50), default);
        Assert.NotNull(a);
        Assert.NotNull(b);
        var c = await t.TryAcquireAsync("p1", "coredev", TimeSpan.FromMilliseconds(20), default);
        Assert.Null(c);
        await a!.DisposeAsync();
        var d = await t.TryAcquireAsync("p1", "coredev", TimeSpan.FromMilliseconds(50), default);
        Assert.NotNull(d);
    }

    [Fact]
    public void Snapshot_ReportsCapAndInFlight()
    {
        var t = new SlotTable();
        t.Configure("p1", "coredev", 3);
        t.Configure("p1", "reviewer", 1);
        var snap = t.Snapshot();
        var byRole = snap.ToDictionary(x => x.Role, x => x);
        Assert.Equal(3, byRole["coredev"].Max);
        Assert.Equal(1, byRole["reviewer"].Max);
        Assert.All(snap, m => Assert.Equal(0, m.InFlight));
    }

    [Fact]
    public async Task Acquire_NotConfigured_Throws()
    {
        var t = new SlotTable();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await t.TryAcquireAsync("ghost", "coredev", TimeSpan.FromMilliseconds(20), default));
    }

    [Fact]
    public async Task Configure_DecreasesCap_StillAcceptsNewAcquire()
    {
        var t = new SlotTable();
        t.Configure("p1", "coredev", 5);
        await t.TryAcquireAsync("p1", "coredev", TimeSpan.FromMilliseconds(50), default);
        t.Configure("p1", "coredev", 2);
        var acquired = await t.TryAcquireAsync("p1", "coredev", TimeSpan.FromMilliseconds(50), default);
        Assert.NotNull(acquired);
        await acquired!.DisposeAsync();
    }

    [Fact]
    public async Task TotalCounters_TickAcrossAcquires()
    {
        var t = new SlotTable();
        t.Configure("p1", "coredev", 2);
        for (var i = 0; i < 5; i++)
        {
            var h = await t.TryAcquireAsync("p1", "coredev", TimeSpan.FromMilliseconds(50), default);
            Assert.NotNull(h);
            await h!.DisposeAsync();
        }
        Assert.Equal(5, t.TotalAcquired);
        Assert.Equal(5, t.TotalReleased);
        Assert.Equal(0, t.InFlight("p1", "coredev"));
    }
}

public class DefaultProjectRolesTests
{
    [Fact]
    public void MaxFor_DefaultsPresent_ForKnownRole()
    {
        Assert.Equal(2, DefaultProjectRoles.MaxFor(new(), "coredev"));
        Assert.Equal(1, DefaultProjectRoles.MaxFor(new(), "intake"));
    }

    [Fact]
    public void MaxFor_PerProjectOverrideWins()
    {
        var roles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["coredev"] = 5 };
        Assert.Equal(5, DefaultProjectRoles.MaxFor(roles, "coredev"));
    }

    [Fact]
    public void MaxFor_UnknownRole_GetsOne()
    {
        Assert.Equal(1, DefaultProjectRoles.MaxFor(new(), "ghost"));
    }
}
