using System.Net;
using Forge.Agents;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// ProviderModelCatalog: the provider /models proxy behind the Agents
/// page model dropdown. OpenAI-shape parsing, auth header, and the
/// degrade-to-empty (never break the editor) contract.
/// </summary>
public class ProviderModelCatalogTests
{
    private static readonly ProviderConfig Provider =
        new("kilo-gateway", "https://gw.example/v1", "test-key", null, "minimax/minimax-m3");

    [Fact]
    public void ParseModelIds_OpenAiShape_ReturnsSortedIds()
    {
        var ids = ProviderModelCatalog.ParseModelIds("""
            {"data":[
                {"id":"z/model","name":"Z"},
                {"id":"kimi-k3"},
                {"id":"minimax/minimax-m3","architecture":{}},
                {"noid":true}
            ]}
            """);
        Assert.Equal(new[] { "kimi-k3", "minimax/minimax-m3", "z/model" }, ids);
    }

    [Fact]
    public void ParseModelIds_Garbage_ReturnsEmpty()
    {
        Assert.Empty(ProviderModelCatalog.ParseModelIds("not json"));
        Assert.Empty(ProviderModelCatalog.ParseModelIds("""{"unexpected":[]}"""));
    }

    [Fact]
    public async Task Fetch_SendsBearerKey_AndParses()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":"kimi-k3"}]}"""),
        });
        var http = new HttpClient(handler);

        var models = await ProviderModelCatalog.FetchModelsAsync(Provider, http, CancellationToken.None);

        Assert.Equal(new[] { "kimi-k3" }, models);
        Assert.Equal("https://gw.example/v1/models", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Fetch_NonSuccessAndFailure_DegradeToEmpty()
    {
        var failing = new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.Empty(await ProviderModelCatalog.FetchModelsAsync(Provider, failing, CancellationToken.None));

        var throwing = new HttpClient(new StubHandler(null!));
        Assert.Empty(await ProviderModelCatalog.FetchModelsAsync(Provider, throwing, CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_UsesModelsUrlOverride_WhenSet()
    {
        // Anthropic-protocol providers whose chat base isn't the
        // OpenAI-shaped root (MiniMax: chat at /anthropic/v1, model
        // listing at /v1/models) carry an explicit modelsUrl.
        var provider = Provider with
        {
            BaseUrl = "https://api.minimax.io/anthropic/v1",
            ModelsUrl = "https://api.minimax.io/v1/models",
        };
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":"MiniMax-M3"}]}"""),
        });
        var http = new HttpClient(handler);

        var models = await ProviderModelCatalog.FetchModelsAsync(provider, http, CancellationToken.None);

        Assert.Equal(new[] { "MiniMax-M3" }, models);
        Assert.Equal("https://api.minimax.io/v1/models", handler.LastRequest!.RequestUri!.ToString());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public HttpRequestMessage? LastRequest { get; private set; }
        public StubHandler(HttpResponseMessage response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (_response is null) throw new HttpRequestException("connection refused");
            return Task.FromResult(_response);
        }
    }
}
