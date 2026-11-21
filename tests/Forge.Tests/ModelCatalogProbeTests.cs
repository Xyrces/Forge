using System.Net;
using System.Text;
using Forge.Agents;
using Xunit;

namespace Forge.Tests;

public sealed class ModelCatalogProbeTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/models/test-model")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/models/test-model")]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/models/test-model")]
    [InlineData("https://gateway.test/api/v1", "https://gateway.test/api/v1/models/test-model")]
    public async Task ProbeOnlyRetrievesConfiguredModelAndSetsAccountHeaders(string baseUrl, string expected)
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var result = await ModelCatalogProbe.CheckAsync(http, new("openai", baseUrl, "fake-key", "org-test", "test-model", Api: "responses", ProjectId: "proj-test"), "test-model");
        Assert.True(result.Success);
        Assert.Equal("GET", handler.Method);
        Assert.Equal(expected, handler.Url);
        Assert.Equal("Bearer fake-key", handler.Auth);
        Assert.Equal("org-test", handler.Org);
        Assert.Equal("proj-test", handler.Project);
        Assert.False(handler.HasBody);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnavailableModelDoesNotFallBackToGeneration()
    {
        var handler = new CaptureHandler { Status = HttpStatusCode.NotFound };
        using var http = new HttpClient(handler);
        var result = await ModelCatalogProbe.CheckAsync(http, new("openai", "https://api.openai.com/v1", "fake", null, "test-model", Api: "responses"), "test-model");
        Assert.False(result.Success); Assert.Equal(1, handler.Calls); Assert.Equal("GET", handler.Method);
    }

    [Fact]
    public async Task UnsupportedLegacyProtocolDoesNotSendAnyRequest()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var result = await ModelCatalogProbe.CheckAsync(http, new("legacy", "https://example.test/v1", "fake", null, "test-model", Api: "anthropic"), "test-model");
        Assert.False(result.Success); Assert.Contains("no supported non-generation", result.Detail); Assert.Equal(0, handler.Calls);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Url, Method, Auth, Org, Project;
        public bool HasBody;
        public int Calls;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Url = request.RequestUri!.AbsoluteUri; Method = request.Method.Method;
            Auth = request.Headers.Authorization?.ToString(); HasBody = request.Content is not null;
            Org = request.Headers.TryGetValues("OpenAI-Organization", out var org) ? org.Single() : null;
            Project = request.Headers.TryGetValues("OpenAI-Project", out var project) ? project.Single() : null;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("{\"id\":\"test-model\"}", Encoding.UTF8, "application/json") });
        }
    }
}
