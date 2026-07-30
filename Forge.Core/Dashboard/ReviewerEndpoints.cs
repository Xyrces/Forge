using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;
using Forge.Reviewer;

namespace Forge.Dashboard;

/// <summary>
/// 2026-07-18 (Phase 2.11.f + bug-2): exposes the Reviewer
/// dispatcher as an HTTP endpoint so the operator (and any
/// out-of-band automation) can trigger a Reviewer run on a
/// specific GitHub PR by number. Returns the Reviewer's verdict.
/// </summary>
public static class ReviewerEndpoints
{
    public static IEndpointRouteBuilder MapReviewerEndpoints(this IEndpointRouteBuilder endpoints, ReviewerDispatcher dispatcher)
    {
        var group = endpoints.MapGroup("/api/forgesystem/reviewer");

        // Resolved via HttpContext.RequestServices so ASP.NET endpoint
        // binding doesn't need to infer ReviewerDispatcher as a
        // route handler parameter (it isn't an enum, primitive, body,
        // or service-registered type by default).
        group.MapPost("/run", async (HttpContext ctx, RunRequest body, ILogger<Reviewer.ReviewerDispatcher> l, CancellationToken ct) =>
        {
            try
            {
                var resolved = ctx.RequestServices.GetService(dispatcher.GetType()) ?? dispatcher;
                var exit = await RunAsync(body, (ReviewerDispatcher)resolved!, l, ct);
                return Results.Ok(new { prNumber = body.PrNumber, exit });
            }
            catch (Exception ex)
            {
                l.LogError(ex, "Reviewer dispatcher crashed for PR #{Pr}", body.PrNumber);
                return Results.Problem(ex.Message, statusCode: 500, title: "reviewer-dispatcher-crashed");
            }
        })
             .WithName("RunReviewer")
             .WithSummary("Run the Reviewer agent against a PR (by number); posts a comment + review on GitHub.");

        return endpoints;
    }

    private static async Task<int> RunAsync(
        RunRequest body,
        ReviewerDispatcher dispatcher,
        ILogger<ReviewerDispatcher> logger,
        CancellationToken ct)
    {
        if (body is null || body.PrNumber <= 0)
            throw new ArgumentException("prNumber must be > 0");

        // The dispatcher doesn't take an `IssueRecord` directly when
        // invoked via HTTP -- we synthesize the minimum metadata it
        // needs from the request body. The full orchestrator
        // integration uses pr-watch issues and is owned by
        // OrchestratorAgent.
        var fakeWatch = new IssueRecord(
            Id: $"pr-watch-body-{body.PrNumber}",
            ShortId: $"pr-watch-body-{body.PrNumber}",
            Type: AgentTaskTypes.PrWatch,
            Title: $"[http] review for pr #{body.PrNumber}",
            Description: null,
            Status: IssueStatus.Pending,
            Priority: 5,
            Assignee: null,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            ClosedAt: null,
            MetadataJson: JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["prNumber"] = body.PrNumber.ToString(),
                ["branch"] = body.Branch ?? "main",
                ["worktreePath"] = "",
                ["taskId"] = $"task-review-{body.PrNumber}",
            }));

        return await dispatcher.ProcessWatchTaskAsync(fakeWatch, ct);
    }
}

internal sealed record RunRequest(
    int PrNumber = 0,
    string? ProjectId = null,
    string? Branch = null);
