using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Forge.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>Real endpoint + local Kestrel; no GitHub, live dashboard or project services.</summary>
public class BuildInfoEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly List<string> _readPaths = new();
    private string _artifactContent = RunningRevisionReaderTests.FullSha;
    private Exception? _readFailure;

    public BuildInfoEndpointTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("buildinfo-ep");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var reader = new RunningRevisionReader(NullLogger.Instance, (path, _) =>
        {
            _readPaths.Add(path);
            return _readFailure is null
                ? Task.FromResult(_artifactContent)
                : Task.FromException<string>(_readFailure);
        });
        app.MapBuildInfoEndpoint(reader);
        _host = app;
        _host.Start();
        _client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/")
        };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        Directory.Delete(_workDir, recursive: true);
    }

    [Theory]
    [MemberData(nameof(RunningRevisionReaderTests.ValidContent), MemberType = typeof(RunningRevisionReaderTests))]
    public async Task BuildInfo_ReturnsFullRevisionAndExactForgeCommitUrl_WithCompatibleExistingFields(string content)
    {
        _artifactContent = content;
        using var body = await GetBuildInfoAsync();
        var revision = content.Trim();

        Assert.Equal(revision, body.RootElement.GetProperty("sourceRevision").GetString());
        Assert.Equal($"https://github.com/Xyrces/Forge/commit/{revision}",
            body.RootElement.GetProperty("commitUrl").GetString());
        Assert.Equal(new[] { "/app/source-revision" }, _readPaths);
    }

    [Theory]
    [MemberData(nameof(RunningRevisionReaderTests.InvalidContent), MemberType = typeof(RunningRevisionReaderTests))]
    public async Task BuildInfo_InvalidContentReturnsExplicitNulls_WithOkAndCompatibleExistingFields(string content)
    {
        _artifactContent = content;
        using var body = await GetBuildInfoAsync();

        AssertUnavailable(body.RootElement);
        Assert.Equal(new[] { "/app/source-revision" }, _readPaths);
    }

    [Theory]
    [MemberData(nameof(RunningRevisionReaderTests.ReadFailures), MemberType = typeof(RunningRevisionReaderTests))]
    public async Task BuildInfo_MissingOrUnreadableArtifactReturnsExplicitNulls_WithoutBreakingEndpoint(Exception failure)
    {
        _readFailure = failure;
        using var body = await GetBuildInfoAsync();

        AssertUnavailable(body.RootElement);
        Assert.Equal(new[] { "/app/source-revision" }, _readPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildInfo_ProjectSelectionAndSwitchingDoNotAffectMetadata(bool unavailable)
    {
        if (unavailable)
            _readFailure = new IOException("Injected artifact read failure");

        string? originalBody = null;
        // No project services are registered: this endpoint must not resolve project state.
        foreach (var query in new[] { "", "?projectId=forge", "?projectId=another-project", "?projectId=forge", "?projectId=", "" })
        {
            using var body = await GetBuildInfoAsync(query);
            var json = body.RootElement.GetRawText();
            originalBody ??= json;
            Assert.Equal(originalBody, json);
            if (unavailable)
                AssertUnavailable(body.RootElement);
            else
            {
                Assert.Equal(RunningRevisionReaderTests.FullSha, body.RootElement.GetProperty("sourceRevision").GetString());
                Assert.Equal($"https://github.com/Xyrces/Forge/commit/{RunningRevisionReaderTests.FullSha}",
                    body.RootElement.GetProperty("commitUrl").GetString());
            }
        }

        Assert.Equal(6, _readPaths.Count);
        Assert.All(_readPaths, path => Assert.Equal("/app/source-revision", path));
    }

    [Fact]
    public async Task BuildInfo_RemainsCompatibleWithLegacyTwoFieldClient()
    {
        var body = await _client.GetFromJsonAsync<BuildInfoShape>("/api/meta/buildinfo");
        Assert.NotNull(body);
        Assert.Equal(ExpectedVersion(), body.InformationalVersion);
        Assert.Equal(RuntimeInformation.FrameworkDescription, body.Framework);
    }

    private async Task<JsonDocument> GetBuildInfoAsync(string query = "")
    {
        using var response = await _client.GetAsync("/api/meta/buildinfo" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal(new[] { "commitUrl", "framework", "informationalVersion", "sourceRevision" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.String, root.GetProperty("informationalVersion").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("framework").ValueKind);
        Assert.Equal(ExpectedVersion(), root.GetProperty("informationalVersion").GetString());
        Assert.Equal(RuntimeInformation.FrameworkDescription, root.GetProperty("framework").GetString());
        return body;
    }

    private static string ExpectedVersion()
    {
        var assembly = typeof(BuildInfoEndpoints).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "0.0.0";
    }

    private static void AssertUnavailable(JsonElement body)
    {
        // GetProperty also proves the fields are present, not omitted by serialization.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sourceRevision").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("commitUrl").ValueKind);
    }

    public sealed class BuildInfoShape
    {
        public string InformationalVersion { get; set; } = string.Empty;
        public string Framework { get; set; } = string.Empty;
    }
}
