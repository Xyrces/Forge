using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Forge.Core;

namespace Forge.Dashboard;

public static class MetaEndpoints
{
    public static void MapMetaEndpoints(this WebApplication app)
    {
        // Enumerate registered /api/* route patterns from
        // EndpointDataSource after endpoints are fully resolved; return
        // as JSON array. RouteEndpoint exposes the compiled
        // RoutePattern whose RawText is the literal template
        // ("/api/foo/{id}"), which is what an operator wants to see.
        // Non-RouteEndpoint entries fall back to DisplayName. We
        // filter to "/api/*" so the response stays scoped to the
        // public HTTP surface.
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
    }
}
