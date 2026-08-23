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
        _dbPath = TempRoot.Instance.NewDbPath("reg");
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
    public async Task LoadAsync_ReadsOnlyFromSqlite()
    {
        // SQLite is the sole source of truth. appsettings.json
        // projects[] is no longer consulted — the loader reads only
        // the project table.
        await _store.UpsertAsync(new NewProject(
            Id: "porthorizon",
            Name: "PortHorizon",
            RepoUrl: "https://github.com/Xyrces/PortHorizon",
            DefaultBranch: "main"));

        var projects = await ProjectRegistryLoader.LoadAsync(_store);
        Assert.Single(projects);
        var p = projects[0];
        Assert.Equal("porthorizon", p.Id);
        Assert.Equal("PortHorizon", p.Name);
        Assert.Equal("https://github.com/Xyrces/PortHorizon", p.RepoUrl);
        Assert.Equal("main", p.DefaultBranch);
        // Root is derived at bootstrap time, not stored in SQLite.
        Assert.Equal(string.Empty, p.Root);
    }

    [Fact]
    public async Task LoadAsync_EmptyStore_ReturnsEmptyList()
    {
        // No synthesis: empty store = empty registry. Operators
        // must add via the dashboard or POST /api/projects.
        var projects = await ProjectRegistryLoader.LoadAsync(_store);
        Assert.Empty(projects);
    }

    [Fact]
    public async Task SeedAsync_LogsAndIgnoresConfigProjects()
    {
        // SeedAsync used to copy appsettings.json into SQLite. The
        // "DB is the single source of truth" iteration removed that
        // — SeedAsync now logs a warning and is a no-op, so legacy
        // callers don't break.
        var config = new ProjectsOptions
        {
            Projects = new List<ProjectOptions>
            {
                new() { Id = "forge", Name = "Forge", RepoUrl = "https://github.com/Xyrces/Forge" },
            },
        };
        await ProjectRegistryLoader.SeedAsync(_store, config, NullLogger.Instance);
        Assert.Empty(await _store.ListAsync());

        // Empty config is also a no-op.
        await ProjectRegistryLoader.SeedAsync(_store, new ProjectsOptions(), NullLogger.Instance);
        Assert.Empty(await _store.ListAsync());
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
    public void NonDefault_GetCanonicalForgeLayout()
    {
        // Non-default projects use the same layout ProjectBootstrap
        // creates: {dataRoot}/projects/{id}/.forge/state — regardless
        // of whether the repo root is operator-managed, so state
        // never lands inside an operator-owned working copy.
        var root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "sdk");
        var p = new ProjectOptions { Id = "suikoden", Root = root };
        Assert.Equal(Path.Combine(DataRoot, "projects", "suikoden", ".forge", "state"), ProjectStateDirs.StateDirFor(p, DataRoot));
        Assert.Equal(Path.Combine(DataRoot, "projects", "suikoden", ".forge", "state", "memory.db"), ProjectStateDirs.MemoryDbFor(p, DataRoot));
    }

    [Fact]
    public void EmptyRootAndRepoUrl_Throws()
    {
        // The legacy "default" project still resolves state under
        // its repo root — without Root or RepoUrl that's impossible.
        var p = new ProjectOptions { Id = "default" };
        Assert.Throws<InvalidOperationException>(() => ProjectStateDirs.StateDirFor(p, DataRoot));
    }

    [Fact]
    public void EmptyRoot_NonDefault_StillResolves()
    {
        // Non-default state lives under {dataRoot}/projects/{id}/
        // .forge/state — derivable from the id alone.
        var p = new ProjectOptions { Id = "x" };
        Assert.Equal(Path.Combine(DataRoot, "projects", "x", ".forge", "state"),
            ProjectStateDirs.StateDirFor(p, DataRoot));
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

public class ProjectStoreRolesTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly ProjectStore _store;

    public ProjectStoreRolesTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("roles");
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

    private async Task<ProjectRecord> SeedAsync(string id = "forge")
        => await _store.UpsertAsync(new NewProject(
            Id: id, Name: id, RepoUrl: $"https://example.com/{id}.git", DefaultBranch: "main"));

    [Fact]
    public async Task NewProject_HasEmptyRoles()
    {
        var p = await SeedAsync();
        Assert.NotNull(p.Roles);
        Assert.Empty(p.Roles);
    }

    [Fact]
    public async Task UpdateRoles_RoundTrips()
    {
        await SeedAsync();
        var roles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["coredev"] = 4,
            ["reviewer"] = 1,
        };
        var updated = await _store.UpdateRolesAsync("forge", roles);
        Assert.True(updated);

        var p = await _store.GetAsync("forge");
        Assert.NotNull(p);
        Assert.Equal(2, p!.Roles.Count);
        Assert.Equal(4, p.Roles["coredev"]);
        Assert.Equal(1, p.Roles["reviewer"]);
    }

    [Fact]
    public async Task UpdateRoles_ReplacesNotMerges()
    {
        await SeedAsync();
        await _store.UpdateRolesAsync("forge", new Dictionary<string, int> { ["coredev"] = 4 });
        await _store.UpdateRolesAsync("forge", new Dictionary<string, int> { ["artist"] = 2 });

        var p = await _store.GetAsync("forge");
        Assert.NotNull(p);
        Assert.Single(p!.Roles);
        Assert.Equal(2, p.Roles["artist"]);
    }

    [Fact]
    public async Task UpdateRoles_UnknownProject_ReturnsFalse()
    {
        var updated = await _store.UpdateRolesAsync("ghost", new Dictionary<string, int> { ["coredev"] = 2 });
        Assert.False(updated);
    }

    [Fact]
    public async Task Upsert_DoesNotClobberExistingRoles()
    {
        await SeedAsync();
        await _store.UpdateRolesAsync("forge", new Dictionary<string, int> { ["coredev"] = 3 });

        // Re-upsert (e.g. rename / repo URL change) must preserve roles_json.
        await _store.UpsertAsync(new NewProject(
            Id: "forge", Name: "Forge Renamed",
            RepoUrl: "https://example.com/forge.git", DefaultBranch: "main"));

        var p = await _store.GetAsync("forge");
        Assert.NotNull(p);
        Assert.Equal("Forge Renamed", p!.Name);
        Assert.Equal(3, p.Roles["coredev"]);
    }

    [Fact]
    public async Task NewProject_TriageDisabledByDefault()
    {
        var p = await SeedAsync();
        Assert.False(p.TriageEnabled);
    }

    [Fact]
    public async Task UpdateTriage_RoundTrips_AndPreservesAllBlocks()
    {
        await SeedAsync();
        await _store.UpdateRolesAsync("forge", new Dictionary<string, int> { ["coredev"] = 4 });
        await _store.UpdateTerritoriesAsync("forge", new Dictionary<string, RoleTerritory>
        {
            ["coredev"] = new(new[] { "Core/" }, AllowsRootFiles: false),
        });
        await _store.UpdateVerifyCommandsAsync("forge", new[] { "dotnet build -c Release" });

        Assert.True(await _store.UpdateTriageAsync("forge", true));
        var p = await _store.GetAsync("forge");
        Assert.NotNull(p);
        Assert.True(p!.TriageEnabled);
        Assert.Equal(4, p.Roles["coredev"]);
        Assert.True(p.Territories.ContainsKey("coredev"));
        Assert.Equal(new[] { "dotnet build -c Release" }, p.VerifyCommands);

        // Toggle back off — the block disappears, siblings survive.
        Assert.True(await _store.UpdateTriageAsync("forge", false));
        p = await _store.GetAsync("forge");
        Assert.False(p!.TriageEnabled);
        Assert.Equal(4, p.Roles["coredev"]);

        // And the other writers preserve an enabled flag.
        Assert.True(await _store.UpdateTriageAsync("forge", true));
        await _store.UpdateRolesAsync("forge", new Dictionary<string, int> { ["reviewer"] = 2 });
        p = await _store.GetAsync("forge");
        Assert.True(p!.TriageEnabled);
        Assert.Equal(2, p.Roles["reviewer"]);
    }

    [Fact]
    public async Task UpdateTriage_UnknownProject_ReturnsFalse()
    {
        Assert.False(await _store.UpdateTriageAsync("ghost", true));
    }

    [Fact]
    public async Task UpdateTerritories_RoundTrips_AndPreservesCaps()
    {
        await SeedAsync();
        await _store.UpdateRolesAsync("forge", new Dictionary<string, int> { ["coredev"] = 3 });

        var territory = new Dictionary<string, RoleTerritory>
        {
            ["coredev"] = new(new[] { "PortHorizon.Core/", "PortHorizon.Tests/", "docs/" }, AllowsRootFiles: true),
            ["clientdev"] = new(new[] { "PortHorizon.Client/" }, AllowsRootFiles: false),
        };
        Assert.True(await _store.UpdateTerritoriesAsync("forge", territory));

        var p = await _store.GetAsync("forge");
        Assert.NotNull(p);
        Assert.Equal(2, p!.Territories.Count);
        Assert.Equal(new[] { "PortHorizon.Core/", "PortHorizon.Tests/", "docs/" }, p.Territories["coredev"].Prefixes);
        Assert.True(p.Territories["coredev"].AllowsRootFiles);
        Assert.False(p.Territories["clientdev"].AllowsRootFiles);
        // Caps survive the territory write.
        Assert.Equal(3, p.Roles["coredev"]);
    }

    [Fact]
    public async Task UpdateRoles_PreservesTerritories()
    {
        await SeedAsync();
        await _store.UpdateTerritoriesAsync("forge", new Dictionary<string, RoleTerritory>
        {
            ["coredev"] = new(new[] { "Src/" }, AllowsRootFiles: false),
        });

        // A caps save (the dashboard's existing PUT) must not drop
        // the territory block stored under the same roles_json column.
        await _store.UpdateRolesAsync("forge", new Dictionary<string, int> { ["coredev"] = 4 });

        var p = await _store.GetAsync("forge");
        Assert.NotNull(p);
        Assert.Equal(4, p!.Roles["coredev"]);
        Assert.Single(p.Territories);
        Assert.Equal(new[] { "Src/" }, p.Territories["coredev"].Prefixes);
    }

    [Fact]
    public async Task UpdateTerritories_UnknownProject_ReturnsFalse()
    {
        var updated = await _store.UpdateTerritoriesAsync("ghost", new Dictionary<string, RoleTerritory>
        {
            ["coredev"] = new(new[] { "Src/" }, AllowsRootFiles: false),
        });
        Assert.False(updated);
    }

    [Fact]
    public async Task LegacyFlatRolesJson_ParsesWithNoTerritories()
    {
        // Rows written before territory existed are a flat role->max
        // dict; they must still parse (caps intact, territories empty).
        await SeedAsync();
        await _store.UpdateRolesAsync("forge", new Dictionary<string, int> { ["coredev"] = 2, ["reviewer"] = 1 });

        var p = await _store.GetAsync("forge");
        Assert.NotNull(p);
        Assert.Equal(2, p!.Roles.Count);
        Assert.Empty(p.Territories);
    }
}

public class RoleTerritoryResolutionTests
{
    private static readonly Agents.RoleAgentRegistry Registry = new();

    [Fact]
    public void ProjectOverride_WinsOverRegistryDefault()
    {
        var roleDef = Registry.ForType(AgentType.CoreDev);
        var territories = new Dictionary<string, RoleTerritory>
        {
            ["coredev"] = new(new[] { "PortHorizon.Core/", "PortHorizon.Tests/" }, AllowsRootFiles: true),
        };

        var (prefixes, rootFiles) = Agents.RoleAgentRegistry.ResolveTerritory(roleDef, territories);

        Assert.Equal(new[] { "PortHorizon.Core/", "PortHorizon.Tests/" }, prefixes);
        Assert.True(rootFiles);
    }

    [Fact]
    public void NoProjectEntry_IsUnconstrained()
    {
        // Contract change 2026-08-12: the registry's built-in
        // territories are Forge-repo-shaped prompt prose, never a gate
        // fallback — an unconfigured project must not have a foreign
        // layout imposed (talaria task-19/20 rejections).
        var roleDef = Registry.ForType(AgentType.CoreDev);
        var territories = new Dictionary<string, RoleTerritory>
        {
            ["clientdev"] = new(new[] { "Elsewhere/" }, AllowsRootFiles: false),
        };

        var (prefixes, rootFiles) = Agents.RoleAgentRegistry.ResolveTerritory(roleDef, territories);

        Assert.Empty(prefixes);
        Assert.False(rootFiles);
    }

    [Fact]
    public void NullProjectTerritories_IsUnconstrained()
    {
        var roleDef = Registry.ForType(AgentType.ClientDev);

        var (prefixes, rootFiles) = Agents.RoleAgentRegistry.ResolveTerritory(roleDef, null);

        Assert.Empty(prefixes);
        Assert.False(rootFiles);
    }

    [Fact]
    public async Task PorthorizonTestPath_PassesTerritoryGate_WithProjectOverride()
    {
        // Regression for the live 2026-07-29 task-7 failure: a plan
        // naming PortHorizon.Tests/... was rejected as "outside
        // coredev's territory" because the gate only knew Forge's
        // repo shape. With the project's override the same plan passes.
        var roleDef = Registry.ForType(AgentType.CoreDev);
        var territories = new Dictionary<string, RoleTerritory>
        {
            ["coredev"] = new(new[] { "PortHorizon.Core/", "PortHorizon.Tests/", "PortHorizon.Benchmarks/", "docs/", ".github/" }, AllowsRootFiles: true),
        };
        var (prefixes, rootFiles) = Agents.RoleAgentRegistry.ResolveTerritory(roleDef, territories);
        var worktree = TempRoot.Instance.NewDirectory("terr");
        Directory.CreateDirectory(Path.Combine(worktree, "PortHorizon.Tests", "Systems"));
        File.WriteAllText(Path.Combine(worktree, "PortHorizon.Tests", "Systems", "MaterialReservationSystemTests.cs"), "// test");
        try
        {
            var gate = new Agents.Gates.PlanTerritoryGate();
            var ctx = new Agents.Gates.RunGateContext(
                TaskId: "task-7",
                RoleName: roleDef.AgentName,
                TerritoryPrefixes: prefixes,
                TerritoryAllowsRootFiles: rootFiles,
                WorktreePath: worktree,
                TaskText: "deflake the test",
                Plan: "## Goal\nDeflake.\n\n## Files\n- PortHorizon.Tests/Systems/MaterialReservationSystemTests.cs\n",
                Ct: CancellationToken.None);

            var verdict = await gate.EvaluateAsync(ctx);

            Assert.Equal(Agents.Gates.GateOutcome.Approve, verdict.Outcome);
        }
        finally
        {
            try { Directory.Delete(worktree, recursive: true); } catch { }
        }
    }
}
