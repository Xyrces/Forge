using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Dashboard;

public static class AppShellEndpoints
{
    public sealed record HeartbeatDto(string Status, DateTime At, string? Version);

    public sealed record BuildInfoDto(string InformationalVersion, string Framework);

    public sealed record SearchHitDto(string Kind, string Id, string Title, string Snippet);

    public sealed record SearchResultsDto(
        IReadOnlyList<SearchHitDto> Issues,
        IReadOnlyList<SearchHitDto> Specs,
        IReadOnlyList<SearchHitDto> Memory);

    public sealed record ActiveSprintDto(string? Id, string? Name);

    public static void MapAppShellEndpoints(
        WebApplication app,
        IIssueStore issues,
        ISprintStore sprints,
        ISpecStore specs,
        MemoryStore? memory,
        ILogger logger,
        Projects.ProjectContextFactory? projectContexts = null)
    {
        app.MapGet("/api/health/ping", () => Results.Json(new { pong = true, at = DateTime.UtcNow.ToString("o") }));

        app.MapGet("/api/health/heartbeat", () =>
        {
            var version = typeof(AppShellEndpoints).Assembly.GetName().Version?.ToString();
            return Results.Json(new HeartbeatDto("healthy", DateTime.UtcNow, version));
        });

        // Build metadata: informationalVersion is resolved from
        // AssemblyInformationalVersionAttribute (the commit-counted
        // version the build pipeline stamps on the assembly); when
        // the attribute is missing we fall back to the assembly
        // version, then to "0.0.0", so the field is always
        // non-empty. framework is the runtime framework string from
        // RuntimeInformation.FrameworkDescription, with "Unknown"
        // as a parseable fallback. Serialized via DashboardJson so
        // the property names stay camelCase (informationalVersion,
        // framework) to match the rest of the dashboard's API
        // surface. Registered MapGet only — non-GET methods auto-
        // 405, locking the read-only contract.
        app.MapGet("/api/meta/buildinfo", () =>
        {
            var assembly = typeof(AppShellEndpoints).Assembly;
            var informationalVersion =
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informationalVersion))
            {
                informationalVersion = assembly.GetName().Version?.ToString();
            }
            if (string.IsNullOrWhiteSpace(informationalVersion))
            {
                informationalVersion = "0.0.0";
            }

            var framework = RuntimeInformation.FrameworkDescription;
            if (string.IsNullOrWhiteSpace(framework))
            {
                framework = "Unknown";
            }

            return Results.Json(new BuildInfoDto(informationalVersion, framework), DashboardJson.Options);
        });

        app.MapGet("/api/sprints/active", async (string? projectId, CancellationToken ct) =>
        {
            // Multi-project: ?projectId= reads that project's sprint
            // store; absent falls back to the injected primary store.
            var store = sprints;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx = projectContexts.Find(projectId);
                if (ctx is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx.Sprints;
            }
            var s = await store.GetActiveAsync(ct);
            return Results.Json(new ActiveSprintDto(s?.Id, s?.Name));
        });

        app.MapGet("/api/search", async (string? q, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            {
                return Results.Json(new SearchResultsDto(Array.Empty<SearchHitDto>(), Array.Empty<SearchHitDto>(), Array.Empty<SearchHitDto>()));
            }

            var like = "%" + q.Trim() + "%";
            var issueHits = await SearchIssuesAsync(issues, like, ct);
            var specHits = await SearchSpecsAsync(specs, like, ct);
            var memHits = memory is null
                ? Array.Empty<SearchHitDto>()
                : await SearchMemoryAsync(memory, q, ct);

            return Results.Json(new SearchResultsDto(issueHits, specHits, memHits));
        });
    }

    private static async Task<IReadOnlyList<SearchHitDto>> SearchIssuesAsync(IIssueStore store, string like, CancellationToken ct)
    {
        try
        {
            var all = await store.ListAsync(new IssueFilter(), ct);
            return all
                .Where(i =>
                    i.Title.Contains(like.Trim('%'), StringComparison.OrdinalIgnoreCase) ||
                    (i.Description?.Contains(like.Trim('%'), StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(5)
                .Select(i => new SearchHitDto("issue", i.Id, i.Title, i.Description ?? ""))
                .ToArray();
        }
        catch
        {
            return Array.Empty<SearchHitDto>();
        }
    }

    private static async Task<IReadOnlyList<SearchHitDto>> SearchSpecsAsync(ISpecStore store, string like, CancellationToken ct)
    {
        try
        {
            var all = await store.ListAsync(null, null, ct);
            return all
                .Where(s => s.Title.Contains(like.Trim('%'), StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .Select(s => new SearchHitDto("spec", s.Id, s.Title, s.Body?.Substring(0, Math.Min(160, s.Body?.Length ?? 0)) ?? ""))
                .ToArray();
        }
        catch
        {
            return Array.Empty<SearchHitDto>();
        }
    }

    private static async Task<IReadOnlyList<SearchHitDto>> SearchMemoryAsync(MemoryStore store, string q, CancellationToken ct)
    {
        try
        {
            var prefix = q.Trim();
            var rows = await store.RecallAsync(prefix, ct);
            return rows
                .Take(5)
                .Select(r => new SearchHitDto("memory", r.Key, r.Key, r.Body?.Substring(0, Math.Min(160, r.Body?.Length ?? 0)) ?? ""))
                .ToArray();
        }
        catch
        {
            return Array.Empty<SearchHitDto>();
        }
    }
}