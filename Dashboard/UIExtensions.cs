using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Fluxor;
using Fluxor.Blazor.Web.ReduxDevTools;
using Forge.Dashboard.Components;
using Forge.Dashboard.Features.AppShell;
using Forge.Dashboard.Features.Specs;
using Forge.Dashboard.Features.Designs;
using Forge.Dashboard.Features.Art;
using Forge.Dashboard.Features.Tasks;

namespace Forge.Dashboard;

public static class UIExtensions
{
    public static IServiceCollection AddForgeUI(this IServiceCollection services, Uri baseAddress)
    {
        var uiAssembly = typeof(App).Assembly;
        services.AddRazorComponents()
            .AddInteractiveServerComponents();
        services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o => o.DetailedErrors = true);
        services.AddFluxor(options =>
            options.ScanAssemblies(uiAssembly)
                   .UseReduxDevTools());
        services.AddHttpClient<AppShellClient>(c => c.BaseAddress = baseAddress);
        services.AddHttpClient<SpecsClient>(c => c.BaseAddress = baseAddress);
        services.AddHttpClient<DesignsClient>(c => c.BaseAddress = baseAddress);
        services.AddHttpClient<ArtClient>(c => c.BaseAddress = baseAddress);
        services.AddHttpClient<TasksClient>(c => c.BaseAddress = baseAddress);
        services.AddHttpClient<Forge.Dashboard.Features.View.ViewClient>(c => c.BaseAddress = baseAddress);
        return services;
    }

    public static WebApplication MapForgeUI(this WebApplication app)
    {
        var candidate = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(App).Assembly.Location)!,
            "..", "..", "..", "Forge.UI", "wwwroot"));
        var uiWwwroot = Directory.Exists(candidate)
            ? candidate
            : Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        if (Directory.Exists(uiWwwroot))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(uiWwwroot),
                OnPrepareResponse = ctx =>
                {
                    if (app.Environment.IsDevelopment())
                    {
                        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                    }
                }
            });
        }

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        return app;
    }
}
