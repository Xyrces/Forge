using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Codebase;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// Phase 2b: codebase graph endpoint. The Intake tab side-panel
/// Graph tab reads from here.
///
/// <para>
/// One endpoint: a build-and-return. The graph is incremental
/// (sha-keyed cache), so repeated calls are cheap when the
/// repo hasn't changed.
/// </para>
/// </summary>
public static class CodebaseGraphEndpoints
{
    public static void MapCodebaseGraphEndpoints(
        WebApplication app,
        ICodebaseGraphBuilder builder,
        ICodebaseGraphCacheStore cacheStore,
        IIssueStore issues,
        ILogger logger)
    {
        // GET /api/codebase-graph?repoRoot=...
        // repoRoot is a query param; we don't take a path param
        // because Windows paths contain backslashes that don't URL-
        // encode cleanly. The dashboard always sends the absolute
        // path from app.config.
        app.MapGet("/api/codebase-graph", async (string repoRoot, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(repoRoot))
                return Results.BadRequest(new { error = "repoRoot required" });
            if (!Directory.Exists(repoRoot))
                return Results.NotFound(new { error = "repoRoot not found", repoRoot });

            CodebaseGraphCache? prior = null;
            try { prior = await cacheStore.GetAsync(repoRoot, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read prior codebase-graph cache; treating as cold");
            }

            CodebaseGraph graph;
            try
            {
                graph = await builder.BuildAsync(repoRoot, prior, cacheDirectory: null, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Codebase graph build failed for {RepoRoot}", repoRoot);
                return Results.Problem(
                    detail: ex.Message,
                    title: "codebase graph build failed",
                    statusCode: 500);
            }

            // Persist manifest.
            try
            {
                await cacheStore.UpsertAsync(new CodebaseGraphCache(
                    BuiltAt: graph.BuiltAt,
                    RepoSha: graph.RepoSha,
                    FileCount: graph.Files.Count,
                    EdgeCount: graph.Imports.Count + graph.Projects.Count,
                    DiskPath: Path.Combine(repoRoot, ".portHorizon", "codebase-graph",
                        Path.GetFileNameWithoutExtension(
                            Directory.GetFiles(Path.Combine(repoRoot, ".portHorizon", "codebase-graph"), "*.json")
                                .FirstOrDefault() ?? "graph") + ".json")
                ), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to upsert codebase_graph_cache row (continuing)");
            }

            return Results.Json(ToView(graph), DashboardJson.Options);
        });
    }

    private static object ToView(CodebaseGraph g) => new
    {
        repoRoot = g.RepoRoot,
        repoSha = g.RepoSha,
        builtAt = g.BuiltAt,
        fileCount = g.Files.Count,
        importCount = g.Imports.Count,
        projectEdgeCount = g.Projects.Count,
        // Files: minimal projection (full node graph would be too
        // big to ship every refresh; UI uses /api/specs/{id}/touches
        // for the overlay).
        files = g.Files.Select(f => new { path = f.Path, module = f.Module, language = f.Language }),
        // Edges for the dashboard's flowchart render.
        imports = g.Imports.Select(e => new { from = e.From, to = e.To }),
        projects = g.Projects.Select(e => new { fromProject = e.FromProject, toProject = e.ToProject })
    };
}