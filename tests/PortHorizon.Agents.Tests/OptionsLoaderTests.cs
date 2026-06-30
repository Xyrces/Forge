using PortHorizon.Agents.Configuration;
using Xunit;

namespace PortHorizon.Agents.Tests;

/// <summary>
/// Regression tests for <see cref="OptionsLoader"/>: the file-loading
/// path is correct, the binder populates string + list + dict
/// properties, and env-var overrides take effect.
/// </summary>
public class OptionsLoaderTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _configPath;

    public OptionsLoaderTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-options-{Guid.NewGuid():N}");
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
        try
        {
            Directory.SetCurrentDirectory(_workDir);
            var options = OptionsLoader.Load();
            Assert.Equal("Xyrces", options.GitHub.Owner);
            Assert.Equal("PortHorizon", options.GitHub.Repo);
            Assert.Equal("gh-test", options.GitHub.Token);
        }
        finally
        {
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
    public void Load_MissingRequiredFields_ThrowsWithAllErrors()
    {
        WriteConfig("{}");
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_workDir);
            var ex = Assert.Throws<InvalidOperationException>(() => OptionsLoader.Load());
            Assert.Contains("Workspace.Root is required", ex.Message);
            Assert.Contains("GitHub.Owner is required", ex.Message);
            Assert.Contains("GitHub.Repo is required", ex.Message);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
        }
    }

    [Fact]
    public void Load_EnvVarLlmApiKey_InjectsProvider()
    {
        // The override path: a single provider entry with the API key from
        // LLM_API_KEY gets injected when the env var is set.
        WriteConfig("""
        {
          "workspace": { "root": "x" },
          "github": { "owner": "o", "repo": "r" },
          "llm": {
            "defaultProvider": "",
            "providers": []
          }
        }
        """);
        var savedCwd = Directory.GetCurrentDirectory();
        Environment.SetEnvironmentVariable("LLM_API_KEY", "kg-env-123");
        Environment.SetEnvironmentVariable("LLM_BASE_URL", "http://kilo.local:9999");
        try
        {
            Directory.SetCurrentDirectory(_workDir);
            var options = OptionsLoader.Load();
            var kilo = options.Llm.Providers.FirstOrDefault(p => p.Name == "kilo-gateway");
            Assert.NotNull(kilo);
            Assert.Equal("kg-env-123", kilo!.ApiKey);
            Assert.Equal("http://kilo.local:9999", kilo.BaseUrl);
            Assert.Equal("kilo-gateway", options.Llm.DefaultProvider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLM_API_KEY", null);
            Environment.SetEnvironmentVariable("LLM_BASE_URL", null);
            Directory.SetCurrentDirectory(savedCwd);
        }
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
