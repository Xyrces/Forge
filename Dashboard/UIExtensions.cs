using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Fluxor;
using Fluxor.Blazor.Web.ReduxDevTools;
using Forge.Dashboard.Components;
using Forge.Dashboard.Features.AppShell;
using Forge.Dashboard.Features.Specs;
using Forge.Dashboard.Features.Designs;
using Forge.Dashboard.Features.Art;

namespace Forge.Dashboard;

public static class UIExtensions
{
    public static IServiceCollection AddForgeUI(this IServiceCollection services)
    {
        var uiAssembly = typeof(App).Assembly;
        services.AddRazorComponents()
            .AddInteractiveServerComponents();
        services.AddRazorPages()
            .AddApplicationPart(uiAssembly);
        services.AddFluxor(options =>
            options.ScanAssemblies(uiAssembly)
                   .UseReduxDevTools());
        services.AddHttpClient<AppShellClient>();
        services.AddHttpClient<SpecsClient>();
        services.AddHttpClient<DesignsClient>();
        services.AddHttpClient<ArtClient>();
        return services;
    }

    public static WebApplication MapForgeUI(this WebApplication app)
    {
        app.MapStaticAssets(staticAssetsManifestPath: "Forge.UI.staticwebassets.endpoints.json");
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        app.MapBlazorHub();
        app.MapFallbackToPage("/_Host");
        return app;
    }
}
