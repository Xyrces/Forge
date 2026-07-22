using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// Surfaces build / runtime metadata for the dashboard.
///   - GET /api/meta/buildinfo  → { informationalVersion, framework }
/// informationalVersion resolves from AssemblyInformationalVersionAttribute
/// (the commit-counted version the build pipeline stamps on the assembly);
/// if the attribute is missing we fall back to AssemblyName.Version and
/// finally to "0.0.0" so the endpoint always returns a non-null string.
/// framework comes from RuntimeInformation.FrameworkDescription, with
/// "Unknown" as the fallback so a misconfigured runtime still produces
/// a parseable payload.
/// </summary>
public static class BuildInfoEndpoints
{
    public sealed record BuildInfoDto(string InformationalVersion, string Framework);

    public static void MapBuildInfoEndpoint(this WebApplication app)
    {
        var assembly = typeof(BuildInfoEndpoints).Assembly;
        var info = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            info = assembly.GetName().Version?.ToString();
        }
        if (string.IsNullOrWhiteSpace(info))
        {
            info = "0.0.0";
        }

        var framework = RuntimeInformation.FrameworkDescription;
        if (string.IsNullOrWhiteSpace(framework))
        {
            framework = "Unknown";
        }

        var dto = new BuildInfoDto(info, framework);

        app.MapGet("/api/meta/buildinfo", () => Results.Json(dto, DashboardJson.Options));
    }
}
