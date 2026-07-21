using Forge.Configuration;
using Forge.Core;
using Forge.Orchestrator.Slots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class ProjectRegistryLoaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly ProjectStore _store;

    public ProjectRegistryLoaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-reg-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _store = new ProjectStore(_issues);
    }

    public void Dispose()
    {
        _store.Dispose();
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public void Load_WithLegacyWorkspaceRoot_ShimsSingleDefaultProject()
    {
        var options = new AgentOptions
        {
            Workspace = new WorkspaceOptions { Root = @"C:\legacy\root" },
        };
        var projects = ProjectRegistryLoader.Load(options, _store, NullLogger.Instance);
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
        var projects = ProjectRegistryLoader.Load(options, _store, NullLogger.Instance);
        Assert.Equal(2, projects.Count);
        Assert.DoesNotContain(projects, p => p.Id == "default");
        Assert.Equal(3, projects.Single(p => p.Id == "suikoden").Roles["coredev"]);
    }

    [Fact]
    public void Load_WithEmptyProjectsAndEmptyWorkspace_SynthesizesAutoScaffoldDefault()
    {
        var options = new AgentOptions();
        var projects = ProjectRegistryLoader.Load(options, _store, NullLogger.Instance);
        var single = Assert.Single(projects);
        Assert.Equal("default", single.Id);
        Assert.Equal(string.Empty, single.Root);
    }

    [Fact]
    public void EnvOverride_TakesPrecedenceOverLegacyRoot()
    {
        var options = new AgentOptions { Workspace = new WorkspaceOptions { Root = @"C:\from-config" } };
        var env = new Dictionary<string, string> { ["FORGE_DEFAULT_PROJECT_ROOT"] = @"C:\from-env" };
        var projects = ProjectRegistryLoader.Load(options, _store, NullLogger.Instance, env);
        Assert.Single(projects);
        Assert.Equal(@"C:\from-env", projects[0].Root);
    }

    [Fact]
    public async Task Load_SqliteProjectsAreAuthoritative()
    {
        await _store.UpsertAsync(new NewProject(
            Id: "porthorizon",
            Name: "From SQLite",
            RepoUrl: "https://github.com/Xyrces/PortHorizon",
            DefaultBranch: "main"));

        var options = new AgentOptions
        {
            Projects = new ProjectsOptions
            {
                Projects = new List<ProjectOptions>
                {
                    new() { Id = "suikoden", Name = "From Config", RepoUrl = "https://example.com/sdk" },
                },
            },
        };

        var projects = ProjectRegistryLoader.Load(options, _store, NullLogger.Instance);
        Assert.Equal(2, projects.Count);
        Assert.Equal("From SQLite", projects.Single(p => p.Id == "porthorizon").Name);
        Assert.Equal("From Config", projects.Single(p => p.Id == "suikoden").Name);
    }

    [Fact]
    public async Task Seed_CopiesConfigProjectsIntoSqlite()
    {
        var config = new ProjectsOptions
        {
            Projects = new List<ProjectOptions>
            {
                new() { Id = "forge", Name = "Forge", RepoUrl = "https://github.com/Xyrces/Forge" },
                new() { Id = "porthorizon", Name = "PortHorizon", RepoUrl = "https://github.com/Xyrces/PortHorizon" },
            },
        };
        await ProjectRegistryLoader.SeedAsync(_store, config, NullLogger.Instance);
        Assert.Equal(2, (await _store.ListAsync()).Count);

        // Idempotent: re-running doesn't duplicate.
        await ProjectRegistryLoader.SeedAsync(_store, config, NullLogger.Instance);
        Assert.Equal(2, (await _store.ListAsync()).Count);
    }
}

public class ProjectStateDirsTests
{
    // Expected paths are built with Path.Combine, not hardcoded '\'
    // literals -- ProjectStateDirs itself is cross-platform (it uses
    // Path.Combine throughout), so the test has to be too, or it fails
    // on Linux CI runners where Path.Combine joins with '/'.
    private const string DataRoot = "/forge/data";

    [Fact]
    public void Default_UsesLegacyFlatLayout()
    {
        var root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "ph");
        var p = new ProjectOptions { Id = "default", Root = root };
        Assert.Equal(Path.Combine(root, ".portHorizon", "state"), ProjectStateDirs.StateDirFor(p, DataRoot));
        Assert.Equal(Path.Combine(root, ".portHorizon", "state", "issues.db"), ProjectStateDirs.IssuesDbFor(p, DataRoot));
    }

    [Fact]
    public void NonDefault_GetPerProjectSubdir()
    {
        var root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "sdk");
        var p = new ProjectOptions { Id = "suikoden", Root = root };
        Assert.Equal(Path.Combine(root, ".portHorizon", "state", "suikoden"), ProjectStateDirs.StateDirFor(p, DataRoot));
        Assert.Equal(Path.Combine(root, ".portHorizon", "state", "suikoden", "memory.db"), ProjectStateDirs.MemoryDbFor(p, DataRoot));
    }

    [Fact]
    public void EmptyRootAndRepoUrl_Throws()
    {
        var p = new ProjectOptions { Id = "x" };
        Assert.Throws<InvalidOperationException>(() => ProjectStateDirs.StateDirFor(p, DataRoot));
    }

    [Fact]
    public void RepoUrlOnly_DerivesFromForgesystemProjectsDir()
    {
        var p = new ProjectOptions { Id = "forge", RepoUrl = "https://github.com/Xyrces/Forge" };
        Assert.Equal(Path.Combine(DataRoot, "projects", "forge"), ProjectStateDirs.RootFor(p, DataRoot));
    }

    [Fact]
    public void ExplicitRoot_BeatsRepoUrlDerivedPath()
    {
        var explicitRoot = "/srv/explicit";
        var p = new ProjectOptions { Id = "forge", RepoUrl = "https://x", Root = explicitRoot };
        Assert.Equal(explicitRoot, ProjectStateDirs.RootFor(p, DataRoot));
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
