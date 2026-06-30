using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using Xunit;

namespace PortHorizon.Agents.Tests;

public class AgentOptionsTests
{
    [Fact]
    public void Default_LlmOptions_HasNoProvidersAndEmptyDefault()
    {
        // No providers configured out of the box; Program.cs falls back to
        // the StubbedChatClientFactory when DefaultProvider is empty.
        var options = new AgentOptions();
        Assert.Empty(options.Llm.Providers);
        Assert.Equal(string.Empty, options.Llm.DefaultProvider);
        Assert.Empty(options.Llm.Roles);
    }

    [Fact]
    public void Default_AgentOptions_AreSafe()
    {
        var options = new AgentOptions();
        Assert.NotNull(options.Workspace);
        Assert.NotNull(options.GitHub);
        Assert.NotNull(options.Spawner);
        Assert.NotNull(options.Dashboard);
        Assert.NotNull(options.Llm);
    }

    [Fact]
    public void AgentOptions_MultiProviderLlmConfig()
    {
        // Two providers, one default, one role override.
        var options = new AgentOptions
        {
            Llm = new LlmOptions
            {
                DefaultProvider = "kilo-gateway",
                Providers = new[]
                {
                    new LlmProviderOptions
                    {
                        Name = "kilo-gateway",
                        BaseUrl = "http://127.0.0.1:4096",
                        ApiKey = "kg-1",
                        DefaultModel = "minimax-m2",
                    },
                    new LlmProviderOptions
                    {
                        Name = "openai",
                        BaseUrl = "https://api.openai.com",
                        ApiKey = "sk-1",
                        DefaultModel = "gpt-5",
                    },
                },
                Roles = new Dictionary<string, LlmRoleModelOptions>
                {
                    ["CoreDev"] = new() { ProviderName = "kilo-gateway", Model = "minimax-m2" },
                    ["Reviewer"] = new() { ProviderName = "openai", Model = "gpt-5" },
                },
            },
        };
        Assert.Equal(2, options.Llm.Providers.Count);
        Assert.Equal("kilo-gateway", options.Llm.DefaultProvider);
        Assert.Equal(2, options.Llm.Roles.Count);
        Assert.Equal("minimax-m2", options.Llm.Roles["CoreDev"].Model);
    }

    [Fact]
    public void LlmProviders_ConstantsAreDistinct()
    {
        var all = new[]
        {
            LlmProviders.Stub,
            LlmProviders.OpenAI,
            LlmProviders.Anthropic,
            LlmProviders.GitHubCopilot,
            LlmProviders.Foundry,
            LlmProviders.KiloGateway,
        };
        Assert.Equal(all.Length, all.Distinct().Count());
    }
}

public class LlmConfigAdapterTests
{
    [Fact]
    public void FromOptions_EmptyOptions_YieldsEmptyConfig()
    {
        var options = new LlmOptions();
        var config = LlmConfigAdapter.FromOptions(options);
        Assert.Empty(config.Providers);
        Assert.Equal(string.Empty, config.DefaultProvider);
        Assert.Empty(config.Roles);
    }

    [Fact]
    public void FromOptions_SingleProvider_NoRole_RoundTrips()
    {
        var options = new LlmOptions
        {
            DefaultProvider = "kilo-gateway",
            Providers = new[]
            {
                new LlmProviderOptions
                {
                    Name = "kilo-gateway",
                    BaseUrl = "http://127.0.0.1:4096",
                    ApiKey = "kg-1",
                    DefaultModel = "minimax-m2",
                },
            },
        };
        var config = LlmConfigAdapter.FromOptions(options);
        var (provider, model) = config.Resolve(AgentType.CoreDev);
        Assert.Equal("kilo-gateway", provider.Name);
        Assert.Equal("minimax-m2", model);
        Assert.Equal("kg-1", provider.ApiKey);
    }

    [Fact]
    public void FromOptions_PerRoleRouting_RoutesToCorrectProviderAndModel()
    {
        var options = new LlmOptions
        {
            DefaultProvider = "kilo-gateway",
            Providers = new[]
            {
                new LlmProviderOptions
                {
                    Name = "kilo-gateway",
                    BaseUrl = "http://127.0.0.1:4096",
                    ApiKey = "kg-1",
                    DefaultModel = "minimax-m2",
                },
                new LlmProviderOptions
                {
                    Name = "openai",
                    BaseUrl = "https://api.openai.com",
                    ApiKey = "sk-1",
                    DefaultModel = "gpt-5",
                },
            },
            Roles = new Dictionary<string, LlmRoleModelOptions>
            {
                ["CoreDev"]   = new() { ProviderName = "kilo-gateway", Model = "minimax-m2" },
                ["ClientDev"] = new() { ProviderName = "kilo-gateway", Model = "mimo 2.5" },
                ["Reviewer"]  = new() { ProviderName = "openai",       Model = "gpt-5" },
            },
        };
        var config = LlmConfigAdapter.FromOptions(options);
        Assert.Equal(("kilo-gateway", "minimax-m2"), (config.Resolve(AgentType.CoreDev).Provider.Name, config.Resolve(AgentType.CoreDev).Model));
        Assert.Equal(("kilo-gateway", "mimo 2.5"), (config.Resolve(AgentType.ClientDev).Provider.Name, config.Resolve(AgentType.ClientDev).Model));
        Assert.Equal(("openai", "gpt-5"), (config.Resolve(AgentType.Reviewer).Provider.Name, config.Resolve(AgentType.Reviewer).Model));
        // QA has no role entry -> falls back to default provider + default model.
        Assert.Equal(("kilo-gateway", "minimax-m2"), (config.Resolve(AgentType.QA).Provider.Name, config.Resolve(AgentType.QA).Model));
    }

    [Fact]
    public void FromOptions_UnknownRole_Throws()
    {
        var options = new LlmOptions
        {
            DefaultProvider = "kilo-gateway",
            Providers = new[] { new LlmProviderOptions { Name = "kilo-gateway", DefaultModel = "minimax-m2" } },
            Roles = new Dictionary<string, LlmRoleModelOptions>
            {
                ["NotARole"] = new() { ProviderName = "kilo-gateway", Model = "minimax-m2" },
            },
        };
        Assert.Throws<InvalidOperationException>(() => LlmConfigAdapter.FromOptions(options));
    }

    [Fact]
    public void FromOptions_RoleReferencesUnknownProvider_Throws()
    {
        var options = new LlmOptions
        {
            DefaultProvider = "kilo-gateway",
            Providers = new[] { new LlmProviderOptions { Name = "kilo-gateway", DefaultModel = "minimax-m2" } },
            Roles = new Dictionary<string, LlmRoleModelOptions>
            {
                ["CoreDev"] = new() { ProviderName = "missing", Model = "x" },
            },
        };
        var config = LlmConfigAdapter.FromOptions(options);
        var ex = Assert.Throws<InvalidOperationException>(() => config.Resolve(AgentType.CoreDev));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class OpenAICompatibleChatClientFactoryTests
{
    [Fact]
    public void Create_EmptyApiKey_ThrowsClearError()
    {
        // No LLM_API_KEY, no apiKey in config; the factory should refuse
        // rather than silently hitting a fake endpoint.
        var provider = new ProviderConfig(
            Name: LlmProviders.KiloGateway,
            BaseUrl: "http://127.0.0.1:4096",
            ApiKey: null,
            OrgId: null,
            DefaultModel: "minimax-m2");
        var config = new LlmConfig(provider);
        using var factory = new OpenAICompatibleChatClientFactory();

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Create(config, AgentType.CoreDev));
        Assert.Contains("ApiKey", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_CachesByProviderAndModel()
    {
        // Two calls with the same provider+model should return the same
        // cached client instance; calls with different models should
        // return different instances.
        var provider = new ProviderConfig(
            Name: LlmProviders.KiloGateway,
            BaseUrl: "http://127.0.0.1:4096",
            ApiKey: "test-key",
            OrgId: null,
            DefaultModel: "model-a");
        var configA = new LlmConfig(provider);
        using var factory = new OpenAICompatibleChatClientFactory();

        var client1 = factory.Create(configA, AgentType.CoreDev);
        var client2 = factory.Create(configA, AgentType.CoreDev);
        Assert.Same(client1, client2);

        // Different default model -> different cache key.
        var providerB = provider with { DefaultModel = "model-b" };
        var configB = new LlmConfig(providerB);
        var client3 = factory.Create(configB, AgentType.CoreDev);
        Assert.NotSame(client1, client3);
    }

    [Fact]
    public void Dispose_ReleasesCachedClients()
    {
        var provider = new ProviderConfig(
            Name: LlmProviders.KiloGateway,
            BaseUrl: "http://127.0.0.1:4096",
            ApiKey: "test-key",
            OrgId: null,
            DefaultModel: "model-a");
        var config = new LlmConfig(provider);
        var factory = new OpenAICompatibleChatClientFactory();
        _ = factory.Create(config, AgentType.CoreDev);

        // Should not throw; cache is cleared on dispose.
        factory.Dispose();
        factory.Dispose(); // idempotent
    }
}
