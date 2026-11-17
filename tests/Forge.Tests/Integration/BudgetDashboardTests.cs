using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Core.Control;
using Forge.Dashboard;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

public sealed class BudgetDashboardTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"configured\":true}")]
    [InlineData("{\"configured\":true,\"generatedAt\":\"2026-11-17T12:00:00Z\",\"limitUsd\":25,\"usageEstimatedUsd\":0,\"conservativeSettledUsd\":0,\"uncertainRequestCount\":0,\"availableAfterHoldsUsd\":25}")]
    public void MissingSpendingFieldsAreNotDisplayedAsZero(string json)
    {
        var data = JsonSerializer.Deserialize<Forge.Dashboard.Features.Budget.BudgetSummary>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Throws<InvalidDataException>(data.Validate);
    }

    [Fact]
    public async Task LegacyAmountsAreSeparatedFromUsageAndNeverCalledBilledCost()
    {
        var store = new ReadOnlyBudget(new(25m, 24.80914m, 0m,
            UsageEstimatedUsd: 0m, ConservativeSettledUsd: 24.80914m));
        await using var app = Host(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}") };
        var body = await client.GetFromJsonAsync<JsonElement>("/api/budget");
        body.Deserialize<Forge.Dashboard.Features.Budget.BudgetSummary>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Validate();
        Assert.Equal("all-projects", body.GetProperty("scope").GetString());
        Assert.False(body.GetProperty("billingReconciled").GetBoolean());
        Assert.Equal(0m, body.GetProperty("usageEstimatedUsd").GetDecimal());
        Assert.Equal(24.80914m, body.GetProperty("conservativeSettledUsd").GetDecimal());
        Assert.Equal(0.19086m, body.GetProperty("availableAfterHoldsUsd").GetDecimal());
        Assert.False(body.TryGetProperty("actualCost", out _));
        Assert.False(body.TryGetProperty("spentUsd", out _));
    }

    [Fact]
    public async Task OperatorReportedPilotCostRestoresAuthorizationWithoutPretendingItWasCalculatedUsage()
    {
        await using var app = Host(new ReadOnlyBudget(new(25m, 1.01m, 0m,
            OperatorReportedUsd: 1.01m, ReconciledOriginalUsd: 24.80914m)));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}") };
        var body = await client.GetFromJsonAsync<JsonElement>("/api/budget");
        Assert.Equal(1.01m, body.GetProperty("operatorReportedUsd").GetDecimal());
        Assert.Equal(0m, body.GetProperty("conservativeSettledUsd").GetDecimal());
        Assert.Equal(0m, body.GetProperty("usageEstimatedUsd").GetDecimal());
        Assert.Equal(23.99m, body.GetProperty("availableAfterHoldsUsd").GetDecimal());
        Assert.False(body.GetProperty("billingReconciled").GetBoolean());
    }

    [Fact]
    public async Task UnknownOutcomesRemainVisibleAndReduceAvailability()
    {
        await using var app = Host(new ReadOnlyBudget(new(25m, 1m, 3m, 1m, 0m, 2)));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}") };
        var body = await client.GetFromJsonAsync<JsonElement>("/api/budget");
        Assert.Equal(21m, body.GetProperty("availableAfterHoldsUsd").GetDecimal());
        Assert.Equal(2, body.GetProperty("uncertainRequestCount").GetInt32());
        Assert.Equal(3m, body.GetProperty("outstandingUsd").GetDecimal());
    }

    [Fact]
    public async Task MissingDatasetIsUnavailableRatherThanZeroSpend()
    {
        await using var app = Host(new ReadOnlyBudget(null));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}") };
        using var response = await client.GetAsync("/api/budget");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task StoreFailureDoesNotExposeDatabaseDetailsOrPretendUnconfigured()
    {
        await using var app = Host(new ReadOnlyBudget(null, fail: true));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}") };
        using var response = await client.GetAsync("/api/budget");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private-database-detail", await response.Content.ReadAsStringAsync());
    }

    private static WebApplication Host(IDurableBudgetStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        BudgetEndpoints.MapBudgetEndpoints(app, store, "dataset", NullLogger.Instance);
        return app;
    }

    private sealed class ReadOnlyBudget(BudgetSnapshot? value, bool fail = false) : IDurableBudgetStore
    {
        public Task<BudgetSnapshot?> ReadAsync(string datasetId, CancellationToken ct = default) =>
            fail ? throw new InvalidOperationException("private-database-detail") : Task.FromResult(value);
        public Task<BudgetReservationResult> ReserveAsync(BudgetReservationRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("A read endpoint must not reserve spending.");
        public Task<bool> CompleteAsync(string datasetId, string requestId, decimal? actualCostUsd, CancellationToken ct = default) =>
            throw new InvalidOperationException("A read endpoint must not change spending.");
    }
}
