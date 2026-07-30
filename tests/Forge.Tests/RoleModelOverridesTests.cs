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
}
