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
        app.MapGet("/api/meta/endpoints", (HttpContext ctx) =>
        {
            var dataSource = ctx.RequestServices.GetRequiredService<EndpointDataSource>();
            var now = DateTimeOffset.UtcNow;
            var items = dataSource.Endpoints
                .Select(e => e is RouteEndpoint re ? re.RoutePattern.RawText : e.DisplayName)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Where(text => text.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Json(new { endpoints = items, generatedAt = now.ToString("o") }, DashboardJson.Options);
        });
    }
}
