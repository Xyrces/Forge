using System.Net;
using Fluxor;
using Fluxor.Blazor.Web.ReduxDevTools;
using Forge.Dashboard;
using Forge.Dashboard.Features.AppShell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Forge.Tests;

public sealed class DashboardFluxorRegistrationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task RenderedDashboardShellDoesNotReferenceTheObsoleteReduxDevToolsAsset(string environmentName)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var address = new Uri("http://offline.invalid/");
        builder.Services.AddForgeUI(builder.Environment, address, address);
        builder.Services.AddHttpClient("ForgeApi").ConfigurePrimaryHttpMessageHandler(() => new OfflineApi());
        await using var app = builder.Build();
        app.UseAntiforgery();
        app.MapForgeUI();
        await app.StartAsync();

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        var html = await client.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/now"));
        Assert.Contains("_framework/blazor.web.js", html);
        Assert.DoesNotContain("_content/Fluxor.Blazor.Web.ReduxDevTools/", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public async Task ReduxDevToolsAreDevelopmentOnlyAndDispatchWorksInEveryEnvironment(
        string environmentName, bool expectDevTools)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        var address = new Uri("http://localhost:4197");
        builder.Services.AddForgeUI(builder.Environment, address, address);
        builder.Services.AddScoped<IJSRuntime, BrowserWithoutExtensions>();
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();

        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<IStore>();
        var devTools = store.GetMiddlewares().OfType<ReduxDevToolsMiddleware>();
        if (expectDevTools)
        {
            var bridge = Assert.Single(devTools).GetClientScripts();
            Assert.Contains("window.__REDUX_DEVTOOLS_EXTENSION__", bridge);
            Assert.Contains("reduxDevTools.connect(", bridge);
        }
        else
        {
            Assert.Empty(devTools);
        }

        await store.InitializeAsync();
        var state = services.GetRequiredService<IState<AppShellState>>();
        Assert.False(state.Value.LiveFeedOpen);
        services.GetRequiredService<IDispatcher>().Dispatch(new AppShellActions.ToggleLiveFeedAction());
        Assert.True(state.Value.LiveFeedOpen);
    }

    private sealed class OfflineApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // No browser circuit is attached to this DI scope, so acknowledge interop calls without executing them.
    private sealed class BrowserWithoutExtensions : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => new(default(TValue)!);
    }
}
