using Forge.Agents;
using Forge.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class ProviderApiKeyResolverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly ProjectStore _projects;
    private readonly SecretStore _secrets;

    public ProviderApiKeyResolverTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("keyres");
        _issues = new IssueStore(_dbPath);
        _projects = new ProjectStore(_issues);
        _secrets = new SecretStore(_issues, DataProtectionProvider.Create("forge.secrets.test"),
            NullLogger<SecretStore>.Instance);
    }

    public void Dispose()
    {
        _secrets.Dispose();
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private ProviderApiKeyResolver NewResolver() =>
        new(_secrets,
            async ct => (await _projects.ListAsync(ct)).Select(p => p.Id).ToArray(),
            NullLogger<ProviderApiKeyResolver>.Instance);

    private async Task SeedProjectWithKeyAsync(string projectId, string kind, string value)
    {
        await _projects.UpsertAsync(new NewProject(projectId, projectId, "https://example.com/" + projectId, "main"));
        await _secrets.UpsertAsync(new NewSecret(projectId, kind, System.Text.Encoding.UTF8.GetBytes(value)));
    }

    [Fact]
    public async Task Refresh_PicksUpRotation_WithoutRestart()
    {
        await SeedProjectWithKeyAsync("porthorizon", "kimi_api_key", "sk-old");
        var resolver = NewResolver();

        await resolver.RefreshAsync(new[] { "kimi" }, CancellationToken.None);
        Assert.Equal("sk-old", resolver.Get("kimi"));

        // Operator rotates the key via the dashboard (re-POST replaces).
        await _secrets.UpsertAsync(new NewSecret("porthorizon", "kimi_api_key", System.Text.Encoding.UTF8.GetBytes("sk-new")));
        await resolver.RefreshAsync(new[] { "kimi" }, CancellationToken.None);
        Assert.Equal("sk-new", resolver.Get("kimi"));
    }

    [Fact]
    public async Task Refresh_DeletedSecret_DropsCachedValue()
    {
        await SeedProjectWithKeyAsync("porthorizon", "kimi_api_key", "sk-live");
        var resolver = NewResolver();
        await resolver.RefreshAsync(new[] { "kimi" }, CancellationToken.None);
        Assert.Equal("sk-live", resolver.Get("kimi"));

        await _secrets.DeleteAsync("porthorizon", "kimi_api_key");
        await resolver.RefreshAsync(new[] { "kimi" }, CancellationToken.None);
        Assert.Null(resolver.Get("kimi"));
    }

    [Fact]
    public async Task Refresh_FindsKeyInAnyProject()
    {
        await _projects.UpsertAsync(new NewProject("forge", "forge", "https://example.com/forge", "main"));
        await SeedProjectWithKeyAsync("porthorizon", "kilo_gateway_api_key", "sk-gw");
        var resolver = NewResolver();

        // Provider name with a dash maps to the underscored secret kind.
        await resolver.RefreshAsync(new[] { "kilo-gateway" }, CancellationToken.None);
        Assert.Equal("sk-gw", resolver.Get("kilo-gateway"));
    }

    [Fact]
    public async Task Get_UnknownProvider_ReturnsNull()
    {
        var resolver = NewResolver();
        await resolver.RefreshAsync(new[] { "kimi" }, CancellationToken.None);
        Assert.Null(resolver.Get("kimi"));
    }
}
