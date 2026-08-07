using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// RoleModelOverrides: the live, DB-backed per-role model assignments
/// edited from the dashboard Agents page. Resolution order is
/// override → llm.roles → provider default, and the sync snapshot is
/// what the run hot path reads.
/// </summary>
public class RoleModelOverridesTests : IDisposable
{
    private readonly string _workDir;
    private readonly MemoryStore _memory;

    public RoleModelOverridesTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("overrides");
        Directory.CreateDirectory(_workDir);
        var bootstrap = new IssueStore(Path.Combine(_workDir, "memory.db"));
        bootstrap.Dispose();
        _memory = new MemoryStore(Path.Combine(_workDir, "memory.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static LlmConfig Config(Dictionary<AgentType, RoleModel>? roles = null) =>
        new(
            Providers: new[]
            {
                new ProviderConfig("kilo-gateway", "http://gw", "key", null, "minimax/minimax-m3"),
                new ProviderConfig("openai", "http://oai", "key", null, "gpt-5"),
            },
            DefaultProvider: "kilo-gateway",
            Roles: roles ?? new Dictionary<AgentType, RoleModel>());

    [Fact]
    public async Task SetThenGet_RoundTrips_ThroughSnapshot()
    {
        var overrides = new RoleModelOverrides(_memory);
        Assert.Null(overrides.Get(AgentType.Reviewer));

        await overrides.SetAsync(AgentType.Reviewer, "kilo-gateway", "kimi-k3");
        var got = overrides.Get(AgentType.Reviewer);
        Assert.Equal("kilo-gateway", got!.ProviderName);
        Assert.Equal("kimi-k3", got.Model);
    }

    [Fact]
    public async Task LoadAsync_RehydratesFromStore()
    {
        var first = new RoleModelOverrides(_memory);
        await first.SetAsync(AgentType.CoreDev, "openai", "gpt-5-mini");

        // A fresh instance (e.g. after restart) sees the persisted override.
        var second = new RoleModelOverrides(_memory);
        Assert.Null(second.Get(AgentType.CoreDev));
        await second.LoadAsync();
        Assert.Equal("gpt-5-mini", second.Get(AgentType.CoreDev)!.Model);
    }

    [Fact]
    public async Task Clear_RemovesOverride_FromStoreAndSnapshot()
    {
        var overrides = new RoleModelOverrides(_memory);
        await overrides.SetAsync(AgentType.QA, "kilo-gateway", "kimi-k3");
        await overrides.ClearAsync(AgentType.QA);
        Assert.Null(overrides.Get(AgentType.QA));

        var fresh = new RoleModelOverrides(_memory);
        await fresh.LoadAsync();
        Assert.Null(fresh.Get(AgentType.QA));
    }

    [Fact]
    public async Task ResolveEffective_OverrideWins_OverRoleConfigAndDefault()
    {
        var overrides = new RoleModelOverrides(_memory);
        var config = Config(new Dictionary<AgentType, RoleModel>
        {
            [AgentType.Reviewer] = new("openai", "gpt-5"),
        });

        // No override: llm.roles entry wins over the provider default.
        var (p1, m1, isOverride1) = config.ResolveEffective(AgentType.Reviewer, overrides);
        Assert.Equal("openai", p1.Name);
        Assert.Equal("gpt-5", m1);
        Assert.False(isOverride1);

        // Override beats both.
        await overrides.SetAsync(AgentType.Reviewer, "kilo-gateway", "kimi-k3");
        var (p2, m2, isOverride2) = config.ResolveEffective(AgentType.Reviewer, overrides);
        Assert.Equal("kilo-gateway", p2.Name);
        Assert.Equal("kimi-k3", m2);
        Assert.True(isOverride2);
    }

    [Fact]
    public async Task ResolveEffective_DanglingOverride_FallsBackToConfig()
    {
        // Override names a provider that is no longer configured —
        // must not throw; configured resolution takes over.
        var overrides = new RoleModelOverrides(_memory);
        await overrides.SetAsync(AgentType.CoreDev, "removed-provider", "some-model");

        var (p, m, isOverride) = Config().ResolveEffective(AgentType.CoreDev, overrides);
        Assert.Equal("kilo-gateway", p.Name);
        Assert.Equal("minimax/minimax-m3", m);
        Assert.False(isOverride);
    }

    [Fact]
    public async Task ProjectOverride_NeverLeaksIntoOtherProjects()
    {
        // Operator rule 2026-07-30: an override set for one project
        // must not change another project's runs.
        var overrides = new RoleModelOverrides(_memory);
        await overrides.SetAsync(AgentType.CoreDev, "openai", "gpt-5-mini", projectId: "porthorizon");

        Assert.Null(overrides.Get(AgentType.CoreDev, "forge"));
        Assert.Equal("gpt-5-mini", overrides.Get(AgentType.CoreDev, "porthorizon")!.Model);
        Assert.Null(overrides.Get(AgentType.CoreDev));            // global: unset
        Assert.Equal("project", overrides.GetScope(AgentType.CoreDev, "porthorizon"));
        Assert.Null(overrides.GetScope(AgentType.CoreDev, "forge"));

        // Resolution: porthorizon gets the override; forge falls to config.
        var config = Config();
        var (pPh, mPh, isOvPh) = config.ResolveEffective(AgentType.CoreDev, overrides, "porthorizon");
        Assert.Equal("openai", pPh.Name);
        Assert.True(isOvPh);
        var (pForge, mForge, isOvForge) = config.ResolveEffective(AgentType.CoreDev, overrides, "forge");
        Assert.Equal("kilo-gateway", pForge.Name);
        Assert.False(isOvForge);
    }

    [Fact]
    public async Task ProjectOverride_WinsOverGlobal_GlobalIsFallback()
    {
        var overrides = new RoleModelOverrides(_memory);
        await overrides.SetAsync(AgentType.Reviewer, "kilo-gateway", "global-model");
        await overrides.SetAsync(AgentType.Reviewer, "openai", "scoped-model", projectId: "porthorizon");

        Assert.Equal("scoped-model", overrides.Get(AgentType.Reviewer, "porthorizon")!.Model);
        Assert.Equal("global-model", overrides.Get(AgentType.Reviewer, "forge")!.Model);
        Assert.Equal("project", overrides.GetScope(AgentType.Reviewer, "porthorizon"));
        Assert.Equal("global", overrides.GetScope(AgentType.Reviewer, "forge"));

        // Clearing the project override re-exposes the global one.
        await overrides.ClearAsync(AgentType.Reviewer, projectId: "porthorizon");
        Assert.Equal("global-model", overrides.Get(AgentType.Reviewer, "porthorizon")!.Model);
    }

    [Fact]
    public async Task LoadAsync_RehydratesScopedKeys()
    {
        var first = new RoleModelOverrides(_memory);
        await first.SetAsync(AgentType.CoreDev, "openai", "global-model");
        await first.SetAsync(AgentType.CoreDev, "openai", "scoped-model", projectId: "porthorizon");

        var second = new RoleModelOverrides(_memory);
        await second.LoadAsync();

        Assert.Equal("scoped-model", second.Get(AgentType.CoreDev, "porthorizon")!.Model);
        Assert.Equal("global-model", second.Get(AgentType.CoreDev, "forge")!.Model);
    }
}
