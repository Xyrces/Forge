using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Forge.Dashboard;

/// <summary>
/// Build/version metadata endpoint. Returns the assembly's
/// informational version and the runtime framework description so
/// operators (and the Blazor UI) can confirm which build is
/// actually serving traffic.
/// </summary>
public static class BuildInfoEndpoints
{
    public sealed record BuildInfoDto(string InformationalVersion, string Framework);

    public static void MapBuildInfoEndpoint(this WebApplication app)
    {
        app.MapGet("/api/meta/buildinfo", () =>
        {
            var dto = new BuildInfoDto(ResolveInformationalVersion(), ResolveFramework());
            return Results.Json(dto, DashboardJson.Options);
        });
    }

    private static string ResolveInformationalVersion()
    {
        var asm = typeof(BuildInfoEndpoints).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info!;
        var asmVer = asm.GetName().Version?.ToString();
        return string.IsNullOrEmpty(asmVer) ? "0.0.0" : asmVer;
    }

    private static string ResolveFramework()
    {
        var framework = RuntimeInformation.FrameworkDescription;
        return string.IsNullOrWhiteSpace(framework) ? "Unknown" : framework;
    }
}
