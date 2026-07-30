using Forge.Configuration;
using Forge.Core;
using Forge.Projects;
using Xunit;

namespace Forge.Tests;

public class GitHubTokenResolverTests
{
    [Fact]
    public async Task PerProjectSecret_OverridesGlobalToken()
    {
        var secrets = new FakeSecretStore();
        secrets.Set("porthorizon", SecretKinds.GitHubToken, "per-project-pat");
        var global = new GitHubOptions { Owner = "o", Repo = "r", Token = "global-pat" };

        var resolved = await GitHubTokenResolver.ResolveAsync("porthorizon", global, secrets);

        Assert.NotNull(resolved);
        Assert.Equal("per-project-pat", resolved!.Token);
        Assert.Equal("o", resolved.Owner);
        Assert.Equal("r", resolved.Repo);
    }

    [Fact]
    public async Task MissingSecret_FallsBackToGlobal()
    {
        var secrets = new FakeSecretStore();
        var global = new GitHubOptions { Token = "global-pat" };

        var resolved = await GitHubTokenResolver.ResolveAsync("porthorizon", global, secrets);

        Assert.Same(global, resolved);
    }

    [Fact]
    public async Task NullSecretStore_ReturnsGlobal()
    {
        var global = new GitHubOptions { Token = "global-pat" };

        var resolved = await GitHubTokenResolver.ResolveAsync("porthorizon", global, secrets: null);

        Assert.Same(global, resolved);
    }

    [Fact]
    public async Task DecryptFailure_FallsBackToGlobal()
    {
        var secrets = new FakeSecretStore { Throws = true };
        var global = new GitHubOptions { Token = "global-pat" };

        var resolved = await GitHubTokenResolver.ResolveAsync("porthorizon", global, secrets);

        Assert.Same(global, resolved);
    }

    [Fact]
    public async Task NullGlobal_WithSecret_ProducesTokenOnlyOptions()
    {
        var secrets = new FakeSecretStore();
        secrets.Set("porthorizon", SecretKinds.GitHubToken, "per-project-pat");

        var resolved = await GitHubTokenResolver.ResolveAsync("porthorizon", global: null, secrets);

        Assert.NotNull(resolved);
        Assert.Equal("per-project-pat", resolved!.Token);
        Assert.Equal(string.Empty, resolved.Owner);
    }

    [Fact]
    public async Task SecretForOtherProject_DoesNotApply()
    {
        var secrets = new FakeSecretStore();
        secrets.Set("forge", SecretKinds.GitHubToken, "forge-pat");
        var global = new GitHubOptions { Token = "global-pat" };

        var resolved = await GitHubTokenResolver.ResolveAsync("porthorizon", global, secrets);

        Assert.Same(global, resolved);
    }

    [Fact]
    public async Task ResolveToken_ExplicitWinsOverSecretAndGlobal()
    {
        var secrets = new FakeSecretStore();
        secrets.Set("forge", SecretKinds.GitHubToken, "secret-pat");
        var global = new GitHubOptions { Token = "global-pat" };

        var token = await GitHubTokenResolver.ResolveTokenAsync("explicit-pat", "forge", global, secrets);
        Assert.Equal("explicit-pat", token);
    }

    [Fact]
    public async Task ResolveToken_SecretWinsOverGlobal()
    {
        var secrets = new FakeSecretStore();
        secrets.Set("forge", SecretKinds.GitHubToken, "secret-pat");
        var global = new GitHubOptions { Token = "global-pat" };

        var token = await GitHubTokenResolver.ResolveTokenAsync(null, "forge", global, secrets);
        Assert.Equal("secret-pat", token);
    }

    [Fact]
    public async Task ResolveToken_GlobalFallback_AndNullWhenNothing()
    {
        var secrets = new FakeSecretStore();
        var global = new GitHubOptions { Token = "global-pat" };

        Assert.Equal("global-pat",
            await GitHubTokenResolver.ResolveTokenAsync(null, "forge", global, secrets));
        Assert.Null(
            await GitHubTokenResolver.ResolveTokenAsync(null, "forge", global: null, secrets));
        Assert.Null(
            await GitHubTokenResolver.ResolveTokenAsync(null, projectId: null, global: null, secrets));
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<(string ProjectId, string Kind), string> _values = new();

        public bool Throws { get; set; }

        public void Set(string projectId, string kind, string plaintext)
            => _values[(projectId, kind)] = plaintext;

        public Task<string?> GetPlaintextAsync(string projectId, string kind, CancellationToken ct = default)
        {
            if (Throws) throw new InvalidOperationException("decrypt failed");
            return Task.FromResult(_values.TryGetValue((projectId, kind), out var v) ? v : (string?)null);
        }

        public Task<SecretRecord?> GetMetadataAsync(string projectId, string kind, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SecretRecord>> ListAsync(string projectId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SecretRecord> UpsertAsync(NewSecret secret, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string projectId, string kind, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
