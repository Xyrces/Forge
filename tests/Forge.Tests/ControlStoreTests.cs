using Forge.Core;
using Forge.Core.Control;
using Forge.Core.Db;
using Forge.Orchestrator.Control;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using Xunit;

namespace Forge.Tests;

public sealed class ControlStoreTests : IDisposable
{
    private readonly IssueStore _issues = new(TempRoot.Instance.NewDbPath("control"));
    private ControlLeaseStore Leases => new(_issues.Db);
    private DurableBudgetStore Budget => new(_issues.Db);
    private static BudgetReservationRequest Request(string id, decimal bound = 1m) =>
        new("dataset", id, "forge", "run", "openai", "model", bound);
    public void Dispose() => _issues.Dispose();

    [Fact]
    public async Task SnapshotSeparatesLegacyConservativeAndUsageEstimatesFromHolds()
    {
        await Leases.InitializeDatasetAsync("dataset", 25m);
        await Budget.ReserveAsync(Request("old")); await Budget.CompleteAsync("dataset", "old", .5m);
        await Budget.ReserveAsync(Request("new"));
        await Budget.CompleteWithUsageAsync("dataset", "new", .01m, "{}", "qualified:usage-estimate");
        await Budget.ReserveAsync(Request("hold")); await Budget.CompleteAsync("dataset", "hold", null);
        var snapshot = (await Budget.ReadAsync("dataset"))!;
        Assert.Equal(.51m, snapshot.SpentUsd); Assert.Equal(.01m, snapshot.UsageEstimatedUsd);
        Assert.Equal(.5m, snapshot.ConservativeSettledUsd); Assert.Equal(1m, snapshot.OutstandingUsd);
        Assert.Equal(1, snapshot.UncertainRequestCount);
    }

    [Fact]
    public async Task UncertainReplayKeepsEvidenceAndCannotReplaceIt()
    {
        await Leases.InitializeDatasetAsync("dataset", 25m);
        await Budget.ReserveAsync(Request("uncertain"));
        await Budget.CompleteWithUsageAsync("dataset", "uncertain", null, "{\"usage\":null}", "unknown-v1");
        await Budget.CompleteAsync("dataset", "uncertain", null);
        var existing = (await Budget.ReserveAsync(Request("uncertain"))).Reservation!;
        Assert.Equal("{\"usage\":null}", existing.UsageJson);
        Assert.Equal("unknown-v1", existing.PricingBasis);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.CompleteWithUsageAsync("dataset", "uncertain", null, "{}", "other"));
        Assert.Equal(1m, (await Budget.ReadAsync("dataset"))!.OutstandingUsd);
    }

    [Fact]
    public async Task UsageSettlementIsDurableAndCannotRepriceCompletedHistory()
    {
        await Leases.InitializeDatasetAsync("dataset", 25m);
        await Budget.ReserveAsync(Request("usage"));
        Assert.True(await Budget.CompleteWithUsageAsync("dataset", "usage", .01m, "{\"usage\":{}}", "tariff-v1"));
        await using var connection = await _issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT usage_json,pricing_basis FROM budget_reservation WHERE request_id='usage'";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); Assert.Equal("{\"usage\":{}}", reader.GetString(0)); Assert.Equal("tariff-v1", reader.GetString(1));
        await reader.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.CompleteWithUsageAsync("dataset", "usage", .005m, "{}", "tariff-v2"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.CompleteWithUsageAsync("dataset", "usage", .01m, "{}", "tariff-v2"));
        Assert.Equal(.01m, (await Budget.ReadAsync("dataset"))!.SpentUsd);
    }

    [Fact]
    public async Task RuntimeFactory_DoesNotAttemptSchemaDdl()
    {
        // No server exists here: constructing and ensuring a runtime store
        // must not open any connection or require DDL permission.
        await using var runtime = new IssueStore(new SqlServerConnectionFactory(
            "Server=does-not-exist.invalid;Database=forge;User ID=unused;Password=unused;Connect Timeout=1",
            "core", ForgeSchemaProfile.Core, initializeSchema: false));
        runtime.EnsureSchema();
    }

    // Uses an actual read-only local database for the SQL catalog query shape.
    // This validates the no-migration contract, not Azure connectivity/permissions.
    private sealed class ReadOnlyCatalogFactory(string path) : IDbConnectionFactory
    {
        private readonly SqliteConnectionFactory _inner = new(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        public bool InitializeSchema => false;
        public ForgeDbProvider Provider => ForgeDbProvider.SqlServer;
        public ISqlDialect Dialect { get; } = new SqlServerDialect("main");
        public ForgeSchemaProfile Profile => ForgeSchemaProfile.Core;
        public string ConnectionString => _inner.ConnectionString;
        public string Qualifier => "main";
        public DbConnection Open() => _inner.Open();
        public Task<DbConnection> OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task StandbyCatalogCheck_IsReadOnlyAndRejectsIncompatibleSchema(int offset)
    {
        var version = SqlServerMigrations.ExpectedVersion + offset;
        await using (var connection = await _issues.Db.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_version; INSERT INTO schema_version(version,applied_at) VALUES (@version,'test')";
            command.AddParam("@version", version);
            await command.ExecuteNonQueryAsync();
        }
        var factory = new ReadOnlyCatalogFactory(_issues.DbPath);
        await using var runtime = new IssueStore(factory);
        runtime.EnsureSchema();
        if (offset == 0) await ControllerHost.VerifySchemaAsync(factory, default);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => ControllerHost.VerifySchemaAsync(factory, default));
        await using var check = await factory.OpenAsync();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT MAX(version) FROM schema_version";
        Assert.Equal(version, Convert.ToInt32(await read.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task DatasetIdentityAndInitialLimit_AreImmutable()
    {
        await Leases.InitializeDatasetAsync("dataset", 25m);
        await Leases.InitializeDatasetAsync("dataset", 25m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Leases.InitializeDatasetAsync("new-dataset", 25m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Leases.InitializeDatasetAsync("dataset", 100m));
        Assert.Equal(25m, (await Leases.ReadAsync("dataset"))!.BudgetLimitUsd);
    }

    [Fact]
    public async Task ConcurrentInitialization_ConvergesOnOneDataset()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Leases.InitializeDatasetAsync("dataset", 25m))));
        Assert.Equal(25m, (await Leases.ReadAsync("dataset"))!.BudgetLimitUsd);
        Assert.Null(await Leases.ReadAsync("other-dataset"));
        Assert.False(await Leases.ReleaseAsync("other-dataset", "pod", 1));
    }

    [Fact]
    public async Task ConcurrentAcquisition_HasOneOwner()
    {
        await Leases.InitializeDatasetAsync("dataset", 25m);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            Leases.TryAcquireAsync("dataset", $"pod-{i}", $"operation-{i}"))));
        var owner = Assert.Single(results.Where(r => r is not null))!;
        Assert.Equal(1, owner.Epoch);
        Assert.NotNull(owner.LastHeartbeatUtc);
        Assert.True(owner.DatabaseUtcNow >= owner.LastHeartbeatUtc);
        Assert.True(await Leases.IsOwnerAsync("dataset", owner.OwnerInstanceId!, owner.Epoch));
    }

    [Fact]
    public async Task ActivationReplay_IsBoundToExactPodAndCannotReactivateReleasedEpoch()
    {
        await Leases.InitializeDatasetAsync("dataset", 25m);
        var first = (await Leases.TryAcquireAsync("dataset", "blue", "op"))!;
        Assert.Equal(first.Epoch, (await Leases.TryAcquireAsync("dataset", "blue", "op"))!.Epoch);
        Assert.Null(await Leases.TryAcquireAsync("dataset", "replacement", "op", "blue"));
        Assert.False(await Leases.ReleaseAsync("dataset", "other", first.Epoch));
        Assert.True(await Leases.ReleaseAsync("dataset", "blue", first.Epoch));
        Assert.Null(await Leases.TryAcquireAsync("dataset", "blue", "op"));
        var next = (await Leases.TryAcquireAsync("dataset", "green", "op2"))!;
        Assert.Equal(first.Epoch + 1, next.Epoch);
        Assert.False(await Leases.RenewAsync("dataset", "blue", first.Epoch));
        Assert.False(await Leases.ReleaseAsync("dataset", "blue", first.Epoch));
        Assert.True(await Leases.RenewAsync("dataset", "green", next.Epoch));
    }

    [Fact]
    public async Task MissingHeartbeat_DoesNotAuthorizeStealing_ExplicitPreviousOwnerDoes()
    {
        await Leases.InitializeDatasetAsync("dataset", 25m);
        var first = (await Leases.TryAcquireAsync("dataset", "blue", "first"))!;
        await using (var connection = await _issues.Db.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE control_dataset SET heartbeat_at=NULL";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Null(await Leases.TryAcquireAsync("dataset", "green", "next"));
        Assert.Null(await Leases.TryAcquireAsync("dataset", "green", "next", "wrong-owner"));
        var next = (await Leases.TryAcquireAsync("dataset", "green", "next", "blue"))!;
        Assert.Equal(first.Epoch + 1, next.Epoch);
        Assert.False(await Leases.IsOwnerAsync("dataset", "blue", first.Epoch));
    }

    [Fact]
    public async Task ConcurrentReservations_CannotOversubscribeLimit()
    {
        await Leases.InitializeDatasetAsync("dataset", 5m);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => Budget.ReserveAsync(Request($"r{i}")))));
        Assert.Equal(5, results.Count(r => r.Admitted && r.IsNew));
        Assert.All(results.Where(r => !r.Admitted), r =>
        {
            Assert.Equal(1m, r.RequestedUsd);
            Assert.Equal(0m, r.AvailableUsd);
        });
        var snapshot = (await Budget.ReadAsync("dataset"))!;
        Assert.Equal(5m, snapshot.OutstandingUsd);
        Assert.Equal(0m, snapshot.SpentUsd);
    }

    [Fact]
    public async Task DuplicateRequest_IsOneLiabilityAndCannotChangeIdentity()
    {
        await Leases.InitializeDatasetAsync("dataset", 25m);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Budget.ReserveAsync(Request("same", 3m)))));
        Assert.All(results, r => Assert.True(r.Admitted));
        Assert.Single(results.Where(r => r.IsNew));
        Assert.Equal(3m, (await Budget.ReadAsync("dataset"))!.OutstandingUsd);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.ReserveAsync(Request("same", 4m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.ReserveAsync(Request("same", 3m) with { RunId = "different" }));
    }

    [Fact]
    public async Task UnknownOrCancelledOutcome_HoldsBoundUntilKnownSettlement()
    {
        await Leases.InitializeDatasetAsync("dataset", 5m);
        await Budget.ReserveAsync(Request("r", 5m));
        Assert.True(await Budget.CompleteAsync("dataset", "r", null));
        Assert.False((await Budget.ReserveAsync(Request("second"))).Admitted);
        Assert.Equal(5m, (await Budget.ReadAsync("dataset"))!.OutstandingUsd);
        Assert.True(await Budget.CompleteAsync("dataset", "r", 2m));
        Assert.True(await Budget.CompleteAsync("dataset", "r", 2m));
        Assert.True(await Budget.CompleteAsync("dataset", "r", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Budget.CompleteAsync("dataset", "r", 0m));
        var snapshot = (await Budget.ReadAsync("dataset"))!;
        Assert.Equal(2m, snapshot.SpentUsd);
        Assert.Equal(0m, snapshot.OutstandingUsd);
        Assert.True((await Budget.ReserveAsync(Request("second", 3m))).Admitted);
    }

    [Fact]
    public async Task ActualOverrun_IsRecordedAndBlocksFurtherSpend()
    {
        await Leases.InitializeDatasetAsync("dataset", 5m);
        await Budget.ReserveAsync(Request("r", 2m));
        await Budget.CompleteAsync("dataset", "r", 6m);
        Assert.Equal(6m, (await Budget.ReadAsync("dataset"))!.SpentUsd);
        Assert.False((await Budget.ReserveAsync(Request("next"))).Admitted);
    }

    [Fact]
    public async Task UninitializedDatasetAndInvalidBounds_FailClosed()
    {
        Assert.False((await Budget.ReserveAsync(Request("r"))).Admitted);
        Assert.Null(await Leases.TryAcquireAsync("dataset", "pod", "op"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Budget.ReserveAsync(Request("zero", 0m)));
        await Leases.InitializeDatasetAsync("dataset", .000001m);
        Assert.True((await Budget.ReserveAsync(Request("tiny", .0000001m))).Admitted);
        Assert.False((await Budget.ReserveAsync(Request("another", .0000001m))).Admitted);
        Assert.False(await Budget.CompleteAsync("dataset", "unknown", 0m));
    }
}
