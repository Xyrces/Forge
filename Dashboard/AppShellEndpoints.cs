using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Dashboard;

public static class AppShellEndpoints
{
    public sealed record HeartbeatDto(string Status, DateTime At, string? Version);

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

        // Enumerate registered /api/* route patterns from
        // EndpointDataSource after endpoints are fully resolved and
        // return them as a JSON array of strings. RouteEndpoint
        // exposes the compiled RoutePattern whose RawText is the
        // literal template ("/api/foo/{id}"), which is what an
        // operator wants to see. Non-RouteEndpoint entries fall
        // back to DisplayName. We filter to "/api/*" so the
        // response stays scoped to the public HTTP surface and
        // deduplicate so overlapping registrations don't show up
        // twice. Registered MapGet only - non-GET methods auto-405,
        // locking the read-only contract.
        app.MapGet("/api/meta/endpoints", (HttpContext ctx) =>
        {
            var dataSource = ctx.RequestServices.GetRequiredService<EndpointDataSource>();
            var items = dataSource.Endpoints
                .Select(e => e is RouteEndpoint re ? re.RoutePattern.RawText : e.DisplayName)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Where(text => text!.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Json(items, DashboardJson.Options);
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