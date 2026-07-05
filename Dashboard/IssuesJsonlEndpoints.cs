using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Forge.Dashboard;

/// <summary>
/// Streams the JSONL mirror of the issue store. Operators can
/// <c>curl http://127.0.0.1:4097/api/issues.jsonl | jq</c> or
/// <c>tail -f</c> the underlying file. See
/// <c>docs/embedded-issues.md</c> Phase 4 for the design.
/// </summary>
public static class IssuesJsonlEndpoints
{
    public static void MapIssuesJsonlEndpoints(
        WebApplication app,
        string path,
        ILogger logger)
    {
        app.MapGet("/api/issues.jsonl", async (HttpContext ctx) =>
        {
            if (!File.Exists(path))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync($"file not found: {path}");
                return;
            }
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            // Disable response buffering so a tail -f-equivalent gets
            // updates as the file is rewritten.
            var buffering = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            buffering?.DisableBuffering();
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            ctx.Response.Headers["X-Jsonl-Path"] = path;
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await fs.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected; normal.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "/api/issues.jsonl streaming failed");
            }
        });

        app.MapGet("/api/issues.jsonl/path", () => Results.Json(new { path }));
    }
}