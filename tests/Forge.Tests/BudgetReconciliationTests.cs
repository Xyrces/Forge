using Forge.Core;
using Forge.Core.Control;
using Xunit;

namespace Forge.Tests;

public sealed class BudgetReconciliationTests : IDisposable
{
    private readonly IssueStore _issues = new(TempRoot.Instance.NewDbPath("reconcile"));
    private DurableBudgetStore Budget => new(_issues.Db);
    private static BudgetReservationRequest Request(string id, decimal bound = 1m) => new("dataset", id, "forge", "run", "OpenAI", "gpt-6.1-sol", bound);
    private static OperatorBudgetReconciliation Reconcile(string id = "pilot", decimal reported = 1.01m) =>
        new("dataset", id, reported, "operator", new string('a', 64), [new("old", 24.80914m)]);
    private async Task Seed()
    {
        await new ControlLeaseStore(_issues.Db).InitializeDatasetAsync("dataset", 25m);
        Assert.True((await Budget.ReserveAsync(Request("old", 24.9m))).Admitted);
        await Budget.CompleteAsync("dataset", "old", 24.80914m);
    }
    public void Dispose() => _issues.Dispose();

    [Fact]
    public async Task MixedInvalidManifestDoesNotLeavePartialCoverage()
    {
        await Seed();
        var invalid = Reconcile() with { Requests = [new("old",24.80914m),new("missing",1m)] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.ReconcileOperatorReportedAsync(invalid));
        Assert.True((await Budget.ReconcileOperatorReportedAsync(Reconcile())).IsNew);
        Assert.Equal(1.01m, (await Budget.ReadAsync("dataset"))!.SpentUsd);
    }

    [Fact]
    public async Task ReservedAndUncertainRequestsCannotBeCoveredAndLateCompletionStillCounts()
    {
        await Seed();
        await Budget.ReserveAsync(Request("reserved", .05m));
        await Budget.ReserveAsync(Request("uncertain", .05m)); await Budget.CompleteAsync("dataset", "uncertain", null);
        foreach (var id in new[] { "reserved", "uncertain" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.ReconcileOperatorReportedAsync(Reconcile() with { Requests = [new(id,.05m)] }));
        await Budget.ReconcileOperatorReportedAsync(Reconcile());
        Assert.Equal(.1m, (await Budget.ReadAsync("dataset"))!.OutstandingUsd);
        await Budget.CompleteAsync("dataset", "reserved", .03m);
        await Budget.CompleteAsync("dataset", "uncertain", .04m);
        var snapshot = (await Budget.ReadAsync("dataset"))!;
        Assert.Equal(1.08m, snapshot.SpentUsd); Assert.Equal(0m, snapshot.OutstandingUsd);
        Assert.Equal(1.01m, snapshot.OperatorReportedUsd);
    }

    [Fact]
    public async Task OperatorReportedCorrectionPreservesRowsAndCapAndChargesFutureWork()
    {
        await Seed();
        Assert.False((await Budget.ReserveAsync(Request("before", 1m))).Admitted);
        var result = await Budget.ReconcileOperatorReportedAsync(Reconcile());
        Assert.True(result.IsNew); Assert.Equal(24.80914m, result.OriginalUsd);
        var snapshot = (await Budget.ReadAsync("dataset"))!;
        Assert.Equal(25m, snapshot.LimitUsd); Assert.Equal(1.01m, snapshot.SpentUsd);
        Assert.Equal(1.01m, snapshot.OperatorReportedUsd); Assert.Equal(24.80914m, snapshot.ReconciledOriginalUsd);
        Assert.Equal(0m, snapshot.ConservativeSettledUsd);
        var old = (await Budget.ReserveAsync(Request("old", 24.9m))).Reservation!;
        Assert.Equal(24.80914m, old.ActualUsd); // Original audit remains unchanged.
        Assert.True((await Budget.ReserveAsync(Request("new", 1m))).Admitted);
        await Budget.CompleteWithUsageAsync("dataset", "new", .02m, "{}", "tariff:usage-estimate");
        snapshot = (await Budget.ReadAsync("dataset"))!;
        Assert.Equal(1.03m, snapshot.SpentUsd); Assert.Equal(.02m, snapshot.UsageEstimatedUsd);
    }

    [Fact]
    public async Task ReplayIsIdempotentButChangedEvidenceOrOverlappingCoverageRejects()
    {
        await Seed(); await Budget.ReconcileOperatorReportedAsync(Reconcile());
        Assert.False((await Budget.ReconcileOperatorReportedAsync(Reconcile())).IsNew);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.ReconcileOperatorReportedAsync(Reconcile(reported: 1m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.ReconcileOperatorReportedAsync(Reconcile() with { EvidenceSha256 = new string('b',64) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.ReconcileOperatorReportedAsync(Reconcile("overlap")));
        Assert.Equal(1.01m, (await Budget.ReadAsync("dataset"))!.SpentUsd);
    }

    [Theory]
    [InlineData("wrong-cost")]
    [InlineData("missing")]
    [InlineData("unfinished")]
    [InlineData("duplicate")]
    public async Task InvalidManifestCannotReleaseLiability(string failure)
    {
        await Seed();
        var request = Reconcile();
        if (failure == "wrong-cost") request = request with { Requests = [new("old", 24m)] };
        if (failure == "missing") request = request with { Requests = [new("missing", 1m)] };
        if (failure == "unfinished")
        {
            await Budget.ReserveAsync(Request("unfinished", .1m));
            request = request with { Requests = [new("unfinished", .1m)] };
        }
        if (failure == "duplicate") request = request with { Requests = [new("old",24.80914m),new("old",24.80914m)] };
        await Assert.ThrowsAnyAsync<Exception>(() => Budget.ReconcileOperatorReportedAsync(request));
        Assert.Equal(24.80914m, (await Budget.ReadAsync("dataset"))!.SpentUsd);
    }

    [Fact]
    public async Task ConcurrentReconciliationAndReservationsCannotExceedUnchangedLimit()
    {
        await Seed();
        var corrections = Enumerable.Range(0, 4).Select(_ => Task.Run(() => Budget.ReconcileOperatorReportedAsync(Reconcile()))).ToArray();
        var reservations = Enumerable.Range(0, 30).Select(i => Task.Run(() => Budget.ReserveAsync(Request("parallel-" + i)))).ToArray();
        await Task.WhenAll(corrections.Cast<Task>().Concat(reservations));
        Assert.Single(corrections.Select(x => x.Result), x => x.IsNew);
        var snapshot = (await Budget.ReadAsync("dataset"))!;
        Assert.Equal(1.01m, snapshot.OperatorReportedUsd);
        Assert.True(snapshot.SpentUsd + snapshot.OutstandingUsd <= 25m);
        Assert.Equal(reservations.Count(x => x.Result.Admitted), snapshot.OutstandingUsd);
        // Fill after races as well: exactly 23 one-dollar holds fit beside the corrected $1.01.
        var filled = 0;
        while ((await Budget.ReserveAsync(Request("fill-" + filled))).Admitted) filled++;
        snapshot = (await Budget.ReadAsync("dataset"))!;
        Assert.Equal(23m, snapshot.OutstandingUsd);
        Assert.Equal(25m, snapshot.LimitUsd);
    }
}
