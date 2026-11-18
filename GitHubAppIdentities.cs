using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Octokit;

namespace Forge;

/// <summary>Controller-only App configuration. A configured repository never falls back to a personal token.</summary>
internal static class GitHubAppIdentities
{
    internal sealed record App(long AppId, long InstallationId, string Slug, string PrivateKeyFile);
    internal sealed record Repository(string Owner, string Name, App Publisher, App Reviewer);
    private sealed record Settings(Repository[] Repositories);
    internal static Repository? FromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 ? Find(parts[0], parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1]) : null;
    }
    internal static Repository? Find(string owner, string name)
    {
        var path = Environment.GetEnvironmentVariable("FORGE_GITHUB_APPS_FILE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("GitHub App configuration is empty.");
        var matches = settings.Repositories.Where(r => string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Duplicate GitHub App repository configuration.");
        if (matches.Length == 0) return null;
        var match = matches[0];
        foreach (var app in new[] { match.Publisher, match.Reviewer })
            if (app.AppId <= 0 || app.InstallationId <= 0 || string.IsNullOrWhiteSpace(app.Slug) || !Path.IsPathFullyQualified(app.PrivateKeyFile))
                throw new InvalidOperationException("Invalid GitHub App identity configuration.");
        if (match.Publisher.AppId == match.Reviewer.AppId || match.Publisher.Slug == match.Reviewer.Slug)
            throw new InvalidOperationException("Publisher and reviewer must be different GitHub Apps.");
        return match;
    }

    internal sealed class InstallationCredentials(Repository repository, App app, bool reviewer, HttpClient? http = null) : ICredentialStore
    {
        private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly SemaphoreSlim _refresh = new(1, 1);
        private Credentials? _credentials;
        private string? _token;
        public async Task<string> GetTokenAsync() { await GetCredentials(); return _token!; }
        private DateTimeOffset _expires;
        public async Task<Credentials> GetCredentials()
        {
            await _refresh.WaitAsync();
            try
            {
                if (_credentials is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(5)) return _credentials;
                var jwt = CreateJwt(app);
                using var installationRequest = Request(HttpMethod.Get, $"app/installations/{app.InstallationId}", jwt);
                using var installationResponse = await (http ?? SharedHttp).SendAsync(installationRequest);
                if (!installationResponse.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub App installation validation failed ({(int)installationResponse.StatusCode}).");
                using var installation = JsonDocument.Parse(await installationResponse.Content.ReadAsStringAsync());
                var root = installation.RootElement;
                if (root.GetProperty("app_id").GetInt64() != app.AppId
                    || !string.Equals(root.GetProperty("app_slug").GetString(), app.Slug, StringComparison.Ordinal)
                    || !string.Equals(root.GetProperty("account").GetProperty("login").GetString(), repository.Owner, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("GitHub App installation does not match the configured owner.");
                using var request = Request(HttpMethod.Post, $"app/installations/{app.InstallationId}/access_tokens", jwt);
                var permissions = new Dictionary<string, string> { ["contents"] = reviewer ? "read" : "write", ["pull_requests"] = "write", ["metadata"] = "read", ["checks"] = "read", ["statuses"] = "read" };
                if (!reviewer) { permissions["issues"] = "write"; permissions["workflows"] = "write"; }
                request.Content = JsonContent.Create(new { repositories = new[] { repository.Name }, permissions });
                using var response = await (http ?? SharedHttp).SendAsync(request);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub App token renewal failed ({(int)response.StatusCode}).");
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var token = body.RootElement.GetProperty("token").GetString();
                _expires = body.RootElement.GetProperty("expires_at").GetDateTimeOffset();
                if (string.IsNullOrWhiteSpace(token) || _expires <= DateTimeOffset.UtcNow.AddMinutes(5))
                    throw new InvalidOperationException("GitHub App returned an unusable installation token.");
                _token = token;
                _credentials = new Credentials(token);
                return _credentials;
            }
            finally { _refresh.Release(); }
        }
        private static HttpRequestMessage Request(HttpMethod method, string path, string jwt)
        {
            var request = new HttpRequestMessage(method, "https://api.github.com/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.UserAgent.ParseAdd("Forge/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            return request;
        }
        private static string CreateJwt(App identity)
        {
            var now = DateTimeOffset.UtcNow;
            var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iat = now.AddSeconds(-60).ToUnixTimeSeconds(), exp = now.AddMinutes(9).ToUnixTimeSeconds(), iss = identity.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(identity.PrivateKeyFile));
            var input = header + "." + payload;
            return input + "." + Encode(rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        private static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
