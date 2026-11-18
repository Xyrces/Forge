using Fluxor;
using Fluxor.Blazor.Web.ReduxDevTools;
using Forge.Dashboard;
using Forge.Dashboard.Features.AppShell;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Forge.Tests;

public sealed class DashboardFluxorRegistrationTests
{
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
            Assert.Single(devTools);
        else
            Assert.Empty(devTools);

        await store.InitializeAsync();
        var state = services.GetRequiredService<IState<AppShellState>>();
        Assert.False(state.Value.LiveFeedOpen);
        services.GetRequiredService<IDispatcher>().Dispatch(new AppShellActions.ToggleLiveFeedAction());
        Assert.True(state.Value.LiveFeedOpen);
    }

    // No browser circuit is attached to this DI scope, so acknowledge interop calls without executing them.
    private sealed class BrowserWithoutExtensions : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => new(default(TValue)!);
    }
}
