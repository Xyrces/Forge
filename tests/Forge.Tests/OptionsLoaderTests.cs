using Forge.Configuration;
using Xunit;

namespace Forge.Tests;

[CollectionDefinition("OptionsLoader process environment", DisableParallelization = true)]
public sealed class OptionsLoaderEnvironmentCollection { }

/// <summary>
/// Regression tests for <see cref="OptionsLoader"/>: the file-loading
/// path is correct, the binder populates string + list + dict
/// properties, and env-var overrides take effect.
/// </summary>
[Collection("OptionsLoader process environment")]
public class OptionsLoaderTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _configPath;

    public OptionsLoaderTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("options");
        Directory.CreateDirectory(_workDir);
        _configPath = Path.Combine(_workDir, "appsettings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private void WriteConfig(string json)
    {
        File.WriteAllText(_configPath, json);
    }

    [Fact]
    public void Load_ReadsWorkspaceRootFromFile()
    {
        WriteConfig("""
        {
          "workspace": { "root": "C:\\test\\workspace" },
          "github": { "owner": "Xyrces", "repo": "PortHorizon" }
        }
        """);
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_workDir);
            var options = OptionsLoader.Load();
            Assert.Equal("C:\\test\\workspace", options.Workspace.Root);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
        }
    }

    [Fact]
    public void Load_ReadsGitHubOwnerAndRepoFromFile()
    {
        WriteConfig("""
        {
          "workspace": { "root": "x" },
          "github": { "owner": "Xyrces", "repo": "PortHorizon", "token": "gh-test" }
        }
        """);
        var savedCwd = Directory.GetCurrentDirectory();
        var savedGhToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var savedGhTokenConfig = Environment.GetEnvironmentVariable("GitHub__Token");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("GitHub__Token", null);
            Directory.SetCurrentDirectory(_workDir);
            var options = OptionsLoader.Load();
            Assert.Equal("Xyrces", options.GitHub.Owner);
            Assert.Equal("PortHorizon", options.GitHub.Repo);
            Assert.Equal("gh-test", options.GitHub.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", savedGhToken);
            Environment.SetEnvironmentVariable("GitHub__Token", savedGhTokenConfig);
            Directory.SetCurrentDirectory(savedCwd);
        }
    }

    [Fact]
    public void Load_ReadsLlmProvidersAndRolesLists()
    {
        WriteConfig("""
        {
          "workspace": { "root": "x" },
          "github": { "owner": "o", "repo": "r" },
          "llm": {
            "defaultProvider": "kilo-gateway",
            "providers": [
              { "name": "kilo-gateway", "baseUrl": "http://127.0.0.1:4096", "defaultModel": "minimax-m2" }
            ],
            "roles": {
              "CoreDev":   { "providerName": "kilo-gateway", "model": "minimax-m2" },
              "ClientDev": { "providerName": "kilo-gateway", "model": "mimo 2.5" }
            }
          }
        }
        """);
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_workDir);
            var options = OptionsLoader.Load();
            Assert.Single(options.Llm.Providers);
            Assert.Equal("kilo-gateway", options.Llm.Providers[0].Name);
            Assert.Equal(2, options.Llm.Roles.Count);
            Assert.Equal("minimax-m2", options.Llm.Roles["CoreDev"].Model);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
        }
    }

    [Fact]
    public void Load_MissingRequiredFields_AcceptsZeroConfigAndAutoScaffolds()
    {
        WriteConfig("{}");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_workDir);
            var options = OptionsLoader.Load();
            Assert.Equal(string.Empty, options.Workspace.Root);
            Assert.NotNull(options.Forgesystem);
            Assert.Equal(string.Empty, options.Forgesystem.DataRoot);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
        }
    }

    private void WriteLlmConfig(string defaultProvider, params LlmProviderOptions[] providers)
    {
        WriteConfig(System.Text.Json.JsonSerializer.Serialize(new
        {
            Llm = new LlmOptions { DefaultProvider = defaultProvider, Providers = providers.ToList() },
        }));
    }

    private static LlmProviderOptions ResponsesProvider() => new()
    {
        Name = "openai",
        ApiKey = "configured-key",
        Api = "responses",
        BaseUrl = "https://api.openai.com/v1",
        DefaultModel = "configured-model",
        OrgId = "configured-org",
        ProjectId = "configured-project",
        ReasoningEffort = "high",
        SharedQuota = true,
        MaxOutputTokens = 1234,
    };

    private AgentOptions LoadWithLlmEnvironment(params (string Name, string? Value)[] overrides)
    {
        // Include hierarchical binder overrides (Llm__*) as well as LLM_*.
        // Never let an ambient worker credential or provider affect these tests.
        var names = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("LLM_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LLM:", StringComparison.OrdinalIgnoreCase))
            .Concat(overrides.Select(entry => entry.Name))
            .Distinct();
        var savedEnv = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            foreach (var name in savedEnv.Keys)
                Environment.SetEnvironmentVariable(name, null);
            foreach (var (name, value) in overrides)
                Environment.SetEnvironmentVariable(name, value);
            Directory.SetCurrentDirectory(_workDir);
            return OptionsLoader.Load();
        }
        finally
        {
            foreach (var (name, value) in savedEnv)
                Environment.SetEnvironmentVariable(name, value);
            Directory.SetCurrentDirectory(savedCwd);
        }
    }

    [Theory]
    [InlineData(null, "openai")]
    [InlineData("", "OPENAI")]
    [InlineData(" \t", "openai")]
    public void Load_EnvVarLlmApiKey_UpdatesConfiguredDefaultAndPreservesTemplate(
        string? providerName, string defaultProvider)
    {
        var template = ResponsesProvider();
        WriteLlmConfig(defaultProvider, template);

        var options = LoadWithLlmEnvironment(
            ("LLM_PROVIDER_NAME", providerName), ("LLM_API_KEY", "env-key"));

        Assert.Equal(defaultProvider, options.Llm.DefaultProvider);
        Assert.Equal(template with { Name = defaultProvider, ApiKey = "env-key" },
            Assert.Single(options.Llm.Providers));
    }

    [Theory]
    [InlineData("alternate")]
    [InlineData("ALTERNATE")]
    public void Load_EnvVarLlmApiKey_ExplicitAlternateUpdatesOnlyMatchingProvider(string providerName)
    {
        var primary = ResponsesProvider();
        var alternate = primary with
        {
            Name = "alternate", ApiKey = "alternate-key", BaseUrl = "https://alternate.test/v1",
            DefaultModel = "alternate-model", ProjectId = "alternate-project",
        };
        WriteLlmConfig("openai", primary, alternate);

        var options = LoadWithLlmEnvironment(
            ("LLM_PROVIDER_NAME", providerName), ("LLM_API_KEY", "env-key"));

        Assert.Equal("openai", options.Llm.DefaultProvider);
        Assert.Equal(new[] { primary, alternate with { Name = providerName, ApiKey = "env-key" } },
            options.Llm.Providers);
    }

    [Fact]
    public void Load_EnvVarLlmApiKey_ExplicitNewProviderDoesNotSwitchConfiguredDefault()
    {
        var primary = ResponsesProvider();
        WriteLlmConfig("openai", primary);

        var options = LoadWithLlmEnvironment(
            ("LLM_PROVIDER_NAME", "alternate"), ("LLM_API_KEY", "env-key"));

        Assert.Equal("openai", options.Llm.DefaultProvider);
        Assert.Equal(new[]
        {
            primary,
            new LlmProviderOptions
            {
                Name = "alternate", ApiKey = "env-key",
                BaseUrl = "http://127.0.0.1:4096", DefaultModel = "stub-model",
            },
        }, options.Llm.Providers);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" \t", " \t")]
    public void Load_EnvVarLlmApiKey_WithoutProviderNamesUsesLegacyFallback(
        string? providerName, string defaultProvider)
    {
        WriteLlmConfig(defaultProvider);

        var options = LoadWithLlmEnvironment(
            ("LLM_PROVIDER_NAME", providerName), ("LLM_API_KEY", "env-key"));

        Assert.Equal("kilo-gateway", options.Llm.DefaultProvider);
        Assert.Equal(new LlmProviderOptions
        {
            Name = "kilo-gateway", ApiKey = "env-key",
            BaseUrl = "http://127.0.0.1:4096", DefaultModel = "stub-model",
        }, Assert.Single(options.Llm.Providers));
    }

    [Fact]
    public void Load_EnvVarLlmApiKey_ExplicitProviderBecomesDefaultWhenUnconfigured()
    {
        WriteLlmConfig(" ");

        var options = LoadWithLlmEnvironment(
            ("LLM_PROVIDER_NAME", "openai"), ("LLM_API_KEY", "env-key"),
            ("LLM_BASE_URL", "https://override.test/v1"), ("LLM_MODEL", "env-model"));

        Assert.Equal("openai", options.Llm.DefaultProvider);
        Assert.Equal(new LlmProviderOptions
        {
            Name = "openai", ApiKey = "env-key",
            BaseUrl = "https://override.test/v1", DefaultModel = "env-model",
        }, Assert.Single(options.Llm.Providers));
    }

    [Fact]
    public void Load_EnvVarLlmApiKey_ExplicitFieldOverridesReplaceTemplateValues()
    {
        var template = ResponsesProvider();
        WriteLlmConfig("openai", template);

        var options = LoadWithLlmEnvironment(
            ("LLM_API_KEY", "env-key"), ("LLM_BASE_URL", "https://override.test/v1"),
            ("LLM_MODEL", "env-model"), ("LLM_API", "openai"),
            ("LLM_ORG_ID", "env-org"), ("LLM_PROJECT_ID", "env-project"),
            ("LLM_REASONING_EFFORT", "low"));

        Assert.Equal("openai", options.Llm.DefaultProvider);
        Assert.Equal(template with
        {
            ApiKey = "env-key", BaseUrl = "https://override.test/v1", DefaultModel = "env-model",
            Api = "openai", OrgId = "env-org", ProjectId = "env-project", ReasoningEffort = "low",
        }, Assert.Single(options.Llm.Providers));
    }

    [Fact]
    public void Load_EnvVarLlmApiKey_PreservesEmptyMatchingTemplateFields()
    {
        var template = ResponsesProvider() with { BaseUrl = "", DefaultModel = "" };
        WriteLlmConfig("openai", template);

        var options = LoadWithLlmEnvironment(("LLM_API_KEY", "env-key"));

        Assert.Equal(template with { ApiKey = "env-key" }, Assert.Single(options.Llm.Providers));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Load_WithoutLlmApiKey_IgnoresSingleProviderOverrides(string? apiKey)
    {
        var template = ResponsesProvider();
        WriteLlmConfig("openai", template);

        var options = LoadWithLlmEnvironment(
            ("LLM_API_KEY", apiKey), ("LLM_PROVIDER_NAME", "alternate"),
            ("LLM_BASE_URL", "https://override.test/v1"), ("LLM_MODEL", "env-model"),
            ("LLM_API", "openai"), ("LLM_ORG_ID", "env-org"),
            ("LLM_PROJECT_ID", "env-project"), ("LLM_REASONING_EFFORT", "low"));

        Assert.Equal("openai", options.Llm.DefaultProvider);
        Assert.Equal(template, Assert.Single(options.Llm.Providers));
    }

    [Fact]
    public void Load_WithoutLlmApiKey_DoesNotCreateProviderOrDefault()
    {
        WriteLlmConfig("");

        var options = LoadWithLlmEnvironment(("LLM_PROVIDER_NAME", "alternate"));

        Assert.Equal("", options.Llm.DefaultProvider);
        Assert.Empty(options.Llm.Providers);
    }

    [Fact]
    public void Load_ConfigPathArg_OverridesDefaultLocation()
    {
        // Passing an explicit --config path works even when the cwd has
        // no appsettings.json (e.g. launched from a parent dir).
        WriteConfig("""
        {
          "workspace": { "root": "from-explicit-path" },
          "github": { "owner": "explicit-owner", "repo": "explicit-repo" }
        }
        """);
        var options = OptionsLoader.Load(_configPath);
        Assert.Equal("from-explicit-path", options.Workspace.Root);
        Assert.Equal("explicit-owner", options.GitHub.Owner);
        Assert.Equal("explicit-repo", options.GitHub.Repo);
    }
}
