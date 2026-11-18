using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Forge.Tests;

public sealed class GitHubAppIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokensAreRepositoryScopedAndCachedWithRolePermissions(bool reviewer)
    {
        using var rsa = RSA.Create(2048);
        var key = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(key, rsa.ExportRSAPrivateKeyPem());
            var app = new GitHubAppIdentities.App(123, 456, "forge-test", key);
            var repo = new GitHubAppIdentities.Repository("owner", "OnlyThisRepo", app, app);
            using var handler = new TokenHandler("owner");
            using var http = new HttpClient(handler);
            var credentials = new GitHubAppIdentities.InstallationCredentials(repo, app, reviewer, http);
            Assert.Equal("test-installation-token", await credentials.GetTokenAsync());
            await credentials.GetCredentials();
            Assert.Equal(2, handler.Calls);
            using var body = JsonDocument.Parse(handler.TokenBody!);
            Assert.Equal(new[] { "OnlyThisRepo" }, body.RootElement.GetProperty("repositories").EnumerateArray().Select(x => x.GetString()).ToArray());
            var permissions = body.RootElement.GetProperty("permissions");
            Assert.Equal(reviewer ? "read" : "write", permissions.GetProperty("contents").GetString());
            Assert.Equal("write", permissions.GetProperty("pull_requests").GetString());
            Assert.Equal(!reviewer, permissions.TryGetProperty("workflows", out _));
            Assert.False(permissions.TryGetProperty("administration", out _));
        }
        finally { File.Delete(key); }
    }

    [Fact]
    public async Task WrongInstallationOwnerFailsBeforeTokenIssuance()
    {
        using var rsa = RSA.Create(2048);
        var key = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(key, rsa.ExportRSAPrivateKeyPem());
            var app = new GitHubAppIdentities.App(123, 456, "forge-test", key);
            using var handler = new TokenHandler("someone-else");
            using var http = new HttpClient(handler);
            var credentials = new GitHubAppIdentities.InstallationCredentials(new("owner", "OnlyThisRepo", app, app), app, true, http);
            await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.GetCredentials());
            Assert.Equal(1, handler.Calls);
            Assert.Null(handler.TokenBody);
        }
        finally { File.Delete(key); }
    }

    private sealed class TokenHandler(string owner) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? TokenBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { app_id = 123, app_slug = "forge-test", account = new { login = owner } }) };
            TokenBody = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.Created) { Content = JsonContent.Create(new { token = "test-installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1) }) };
        }
    }
}
