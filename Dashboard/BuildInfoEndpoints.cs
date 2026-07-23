using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Runtime.InteropServices;
using System.Reflection;

namespace Forge.Dashboard;

public static class BuildInfoEndpoints
{
    public sealed record BuildInfoDto(string InformationalVersion, string Framework);

    public static void MapBuildInfoEndpoint(this WebApplication app)
    {
        app.MapGet("/api/meta/buildinfo", () =>
        {
            var assembly = typeof(BuildInfoEndpoints).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString()
                          ?? "0.0.0";
            var framework = RuntimeInformation.FrameworkDescription ?? "Unknown";
            return Results.Json(new BuildInfoDto(version, framework), DashboardJson.Options);
        });
    }
}
