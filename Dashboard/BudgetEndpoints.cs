using Forge.Core.Control;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Forge.Dashboard;

/// <summary>Dataset-wide spending authorization. These estimates are not a provider invoice.</summary>
public static class BudgetEndpoints
{
    public static void MapBudgetEndpoints(WebApplication app, IDurableBudgetStore? store,
        string? datasetId, ILogger logger)
    {
        app.MapGet("/api/budget", async (CancellationToken ct) =>
        {
            if (store is null || string.IsNullOrWhiteSpace(datasetId))
                return Results.Ok(new { configured = false });
            try
            {
                var snapshot = await store.ReadAsync(datasetId, ct);
                if (snapshot is null)
                    return Results.Problem("Spending authorization is unavailable.", statusCode: 503);
                return Results.Ok(new
                {
                    configured = true,
                    scope = "all-projects",
                    currency = "USD",
                    generatedAt = DateTime.UtcNow,
                    limitUsd = snapshot.LimitUsd,
                    usageEstimatedUsd = snapshot.UsageEstimatedUsd,
                    operatorReportedUsd = snapshot.OperatorReportedUsd,
                    conservativeSettledUsd = snapshot.ConservativeSettledUsd,
                    outstandingUsd = snapshot.OutstandingUsd,
                    uncertainRequestCount = snapshot.UncertainRequestCount,
                    availableAfterHoldsUsd = Math.Max(0m,
                        snapshot.LimitUsd - snapshot.SpentUsd - snapshot.OutstandingUsd),
                    billingReconciled = false,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unable to read spending authorization");
                return Results.Problem("Spending information could not be loaded. Retry shortly.", statusCode: 503);
            }
        });
    }
}
