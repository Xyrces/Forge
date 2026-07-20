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
using Forge.Dashboard.Features.Projects;
using Forge.Dashboard.Features.Deployments;

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
        services.AddHttpClient<ProjectsClient>(c => c.BaseAddress = baseAddress);
        services.AddHttpClient<DeploymentsClient>(c => c.BaseAddress = baseAddress);
        return services;
    }

    public static WebApplication MapForgeUI(this WebApplication app)
    {
        // Resolve the UI's wwwroot directory. When running from source
        // (dotnet run) the App assembly lives in bin/Debug/net10.0/Forge.UI.dll
        // and ../../../Forge.UI/wwwroot lands in the source tree. When
        // running as the published SCM binary the assembly lives in
        // C:\ProgramData\Forge\current\Forge.UI.dll and the same
        // relative path lands at C:\Forge.UI\wwwroot (wrong). The
        // publish step doesn't copy the static files into the release
        // dir, so fall back to looking in:
        //   1. <AppContext.BaseDirectory>/wwwroot      -- future: when we
        //      publish the static files into the binary dir
        //   2. <repo-root>/Forge.UI/wwwroot             -- dev / fallback:
        //      walk up from the binary dir to find the repo root via
        //      *.sln file marker, then descend into Forge.UI/wwwroot.
        // Without one of these, app.css / app.js / _framework/blazor.web.js
        // are not served and the dashboard renders without styling or
        // interactive component support.
        var uiWwwroot = ResolveUiWwwroot();
        if (uiWwwroot is not null)
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

    private static string? ResolveUiWwwroot()
    {
        // 1. Published binary next to wwwroot
        var baseDir = AppContext.BaseDirectory;
        var sibling = Path.Combine(baseDir, "wwwroot");
        if (Directory.Exists(sibling) && File.Exists(Path.Combine(sibling, "app.css")))
            return sibling;

        // 2. Walk up from the binary dir looking for a *.sln file,
        //    then descend into Forge.UI/wwwroot.
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var sln = dir.EnumerateFiles("*.sln").FirstOrDefault();
            if (sln is not null)
            {
                var candidate = Path.Combine(dir.FullName, "Forge.UI", "wwwroot");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "app.css")))
                    return candidate;
                // Found the sln but the UI wwwroot doesn't exist there
                // -- this means a clean checkout without UI assets.
                return null;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
