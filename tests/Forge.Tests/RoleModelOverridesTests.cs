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

    // ---- Phase 3: the escalation tier ----

    [Fact]
    public async Task Escalation_SetThenGet_RoundTrips_IndependentOfModelTier()
    {
        var overrides = new RoleModelOverrides(_memory);
        Assert.Null(overrides.GetEscalation(AgentType.CoreDev));

        await overrides.SetEscalationAsync(AgentType.CoreDev, "openai", "gpt-5-pro");
        Assert.Equal("gpt-5-pro", overrides.GetEscalation(AgentType.CoreDev)!.Model);
        // The tiers never collide: the model override stays unset.
        Assert.Null(overrides.Get(AgentType.CoreDev));
        Assert.Equal("global", overrides.GetEscalationScope(AgentType.CoreDev, "forge"));
    }

    [Fact]
    public async Task Escalation_LoadAsync_Rehydrates()
    {
        var first = new RoleModelOverrides(_memory);
        await first.SetEscalationAsync(AgentType.CoreDev, "openai", "esc-global");
        await first.SetEscalationAsync(AgentType.CoreDev, "kilo-gateway", "esc-scoped", projectId: "porthorizon");

        var second = new RoleModelOverrides(_memory);
        await second.LoadAsync();

        Assert.Equal("esc-scoped", second.GetEscalation(AgentType.CoreDev, "porthorizon")!.Model);
        Assert.Equal("esc-global", second.GetEscalation(AgentType.CoreDev, "forge")!.Model);
        Assert.Equal("project", second.GetEscalationScope(AgentType.CoreDev, "porthorizon"));
    }

    [Fact]
    public async Task Escalation_ProjectOverride_NeverLeaksIntoOtherProjects()
    {
        var overrides = new RoleModelOverrides(_memory);
        await overrides.SetEscalationAsync(AgentType.CoreDev, "openai", "esc-scoped", projectId: "porthorizon");

        Assert.Null(overrides.GetEscalation(AgentType.CoreDev, "forge"));
        Assert.Null(overrides.GetEscalation(AgentType.CoreDev));
        Assert.Equal("esc-scoped", overrides.GetEscalation(AgentType.CoreDev, "porthorizon")!.Model);
    }

    [Fact]
    public async Task ResolveEscalation_Unset_WhenNothingConfigured()
    {
        // Explicit-only escalation: no override + no config entry =
        // NO provider-default fallback.
        var overrides = new RoleModelOverrides(_memory);
        Assert.Null(Config().ResolveEscalationEffective(AgentType.CoreDev, overrides, "forge"));
    }

    [Fact]
    public async Task ResolveEscalation_ResolutionOrder_ProjectOverride_GlobalOverride_Config()
    {
        var overrides = new RoleModelOverrides(_memory);
        var config = new LlmConfig(
            Providers: new[]
            {
                new ProviderConfig("kilo-gateway", "http://gw", "key", null, "minimax/minimax-m3"),
                new ProviderConfig("openai", "http://oai", "key", null, "gpt-5"),
            },
            DefaultProvider: "kilo-gateway",
            Roles: new Dictionary<AgentType, RoleModel>(),
            EscalationRoles: new Dictionary<AgentType, RoleModel>
            {
                [AgentType.CoreDev] = new("kilo-gateway", "esc-config"),
            });

        // Config entry only.
        var r1 = config.ResolveEscalationEffective(AgentType.CoreDev, overrides, "porthorizon");
        Assert.Equal("esc-config", r1!.Value.Model);
        Assert.False(r1.Value.IsOverride);

        // Global override beats config.
        await overrides.SetEscalationAsync(AgentType.CoreDev, "openai", "esc-global");
        var r2 = config.ResolveEscalationEffective(AgentType.CoreDev, overrides, "porthorizon");
        Assert.Equal("esc-global", r2!.Value.Model);
        Assert.True(r2.Value.IsOverride);

        // Project override beats global.
        await overrides.SetEscalationAsync(AgentType.CoreDev, "openai", "esc-scoped", projectId: "porthorizon");
        var r3 = config.ResolveEscalationEffective(AgentType.CoreDev, overrides, "porthorizon");
        Assert.Equal("esc-scoped", r3!.Value.Model);
        // Another project still sees the global override.
        var r4 = config.ResolveEscalationEffective(AgentType.CoreDev, overrides, "forge");
        Assert.Equal("esc-global", r4!.Value.Model);
    }

    [Fact]
    public async Task ResolveEscalation_DanglingOverride_FallsToConfig_ThenUnset()
    {
        var overrides = new RoleModelOverrides(_memory);
        await overrides.SetEscalationAsync(AgentType.CoreDev, "removed-provider", "esc-gone");

        // No config entry → unset (never the provider default).
        Assert.Null(Config().ResolveEscalationEffective(AgentType.CoreDev, overrides, "forge"));

        // Config entry catches the fall.
        var config = new LlmConfig(
            Providers: new[] { new ProviderConfig("kilo-gateway", "http://gw", "key", null, "m") },
            DefaultProvider: "kilo-gateway",
            Roles: new Dictionary<AgentType, RoleModel>(),
            EscalationRoles: new Dictionary<AgentType, RoleModel>
            {
                [AgentType.CoreDev] = new("kilo-gateway", "esc-config"),
            });
        Assert.Equal("esc-config",
            config.ResolveEscalationEffective(AgentType.CoreDev, overrides, "forge")!.Value.Model);
    }

    [Fact]
    public async Task ResolveEscalation_DanglingProjectOverride_FallsToGlobalOverride_NotConfig()
    {
        // Each override tier gets its OWN provider-validity check: a
        // dangling project-scoped override falls to the GLOBAL
        // override, never straight to config.
        var overrides = new RoleModelOverrides(_memory);
        await overrides.SetEscalationAsync(AgentType.CoreDev, "removed-provider", "esc-dangling", projectId: "porthorizon");
        await overrides.SetEscalationAsync(AgentType.CoreDev, "openai", "esc-global");

        var config = new LlmConfig(
            Providers: new[]
            {
                new ProviderConfig("kilo-gateway", "http://gw", "key", null, "m"),
                new ProviderConfig("openai", "http://oai", "key", null, "gpt-5"),
            },
            DefaultProvider: "kilo-gateway",
            Roles: new Dictionary<AgentType, RoleModel>(),
            EscalationRoles: new Dictionary<AgentType, RoleModel>
            {
                [AgentType.CoreDev] = new("kilo-gateway", "esc-config"),
            });

        var resolved = config.ResolveEscalationEffective(AgentType.CoreDev, overrides, "porthorizon");
        Assert.Equal("esc-global", resolved!.Value.Model);
        Assert.True(resolved.Value.IsOverride);
    }

    [Fact]
    public async Task Escalation_Clear_RemovesFromStoreAndSnapshot()
    {
        var overrides = new RoleModelOverrides(_memory);
        await overrides.SetEscalationAsync(AgentType.CoreDev, "openai", "esc");
        await overrides.ClearEscalationAsync(AgentType.CoreDev);
        Assert.Null(overrides.GetEscalation(AgentType.CoreDev));

        var fresh = new RoleModelOverrides(_memory);
        await fresh.LoadAsync();
        Assert.Null(fresh.GetEscalation(AgentType.CoreDev));
    }

    // ---- Phase 3 inheritance cut: per-role independence ----

    [Fact]
    public async Task ResolveEffective_OverrideForOneRole_NeverAppliesToAnother()
    {
        // The inheritance cut means designer/groomer/artist resolve
        // independently: a CoreDev override must not leak into the
        // groomer's resolution.
        var overrides = new RoleModelOverrides(_memory);
        await overrides.SetAsync(AgentType.CoreDev, "openai", "gpt-5-mini");

        var config = Config();
        var (pCore, mCore, _) = config.ResolveEffective(AgentType.CoreDev, overrides, "forge");
        Assert.Equal("gpt-5-mini", mCore);
        var (pGroomer, mGroomer, isOverrideGroomer) = config.ResolveEffective(AgentType.Groomer, overrides, "forge");
        Assert.Equal("minimax/minimax-m3", mGroomer);
        Assert.False(isOverrideGroomer);
    }

    [Fact]
    public void Adapter_MapsEscalationModel_AndPipelineRoleKeys()
    {
        // llm.roles.<AgentType>.escalationModel lands in
        // LlmConfig.EscalationRoles; the new pipeline AgentTypes
        // (Designer/Groomer/Artist) are valid role keys.
        var options = new Forge.Configuration.LlmOptions
        {
            DefaultProvider = "kilo-gateway",
            Providers =
            {
                new Forge.Configuration.LlmProviderOptions
                { Name = "kilo-gateway", BaseUrl = "http://gw", DefaultModel = "minimax/minimax-m3" },
                new Forge.Configuration.LlmProviderOptions
                { Name = "openai", BaseUrl = "http://oai", DefaultModel = "gpt-5" },
            },
            Roles =
            {
                ["CoreDev"] = new Forge.Configuration.LlmRoleModelOptions
                {
                    ProviderName = "kilo-gateway",
                    Model = "minimax/minimax-m3",
                    EscalationModel = new Forge.Configuration.LlmRoleModelOptions
                    { ProviderName = "openai", Model = "gpt-5-pro" },
                },
                ["Groomer"] = new Forge.Configuration.LlmRoleModelOptions
                { ProviderName = "kilo-gateway", Model = "kimi-k3" },
            },
        };

        var config = LlmConfigAdapter.FromOptions(options);

        Assert.Equal("kimi-k3", config.Roles[AgentType.Groomer].Model);
        Assert.Equal("gpt-5-pro", config.EscalationRoles![AgentType.CoreDev].Model);
        Assert.Equal("openai", config.EscalationRoles[AgentType.CoreDev].ProviderName);
        Assert.False(config.EscalationRoles.ContainsKey(AgentType.Groomer));
        // The groomer resolves its OWN entry, not coredev's.
        var (_, groomerModel) = config.Resolve(AgentType.Groomer);
        Assert.Equal("kimi-k3", groomerModel);
    }
}
