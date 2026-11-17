using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Runtime.InteropServices;
using System.Reflection;

namespace Forge.Dashboard;

public static class BuildInfoEndpoints
{
    public sealed record BuildInfoDto(string InformationalVersion, string Framework,
        string? SourceRevision = null, string? CommitUrl = null);

    public static void MapBuildInfoEndpoint(this WebApplication app, RunningRevisionReader? revisionReader = null)
    {
        var reader = revisionReader ?? new RunningRevisionReader(app.Logger);
        app.MapGet("/api/meta/buildinfo", async (CancellationToken ct) =>
        {
            var assembly = typeof(BuildInfoEndpoints).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString()
                          ?? "0.0.0";
            var framework = RuntimeInformation.FrameworkDescription ?? "Unknown";
            var revision = await reader.ReadAsync(ct);
            var commitUrl = revision is null ? null : $"https://github.com/Xyrces/Forge/commit/{revision}";
            return Results.Json(new BuildInfoDto(version, framework, revision, commitUrl), DashboardJson.Options);
        });
    }
}
