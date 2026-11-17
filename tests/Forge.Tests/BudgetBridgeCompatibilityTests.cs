using Forge.Core;
using Forge.Core.Control;
using Forge.Core.Db;
using Xunit;

namespace Forge.Tests;

/// <summary>Exercises the unchanged schema-38 stores against the additive schema-39 shape.
/// SQLite verifies query shape and accounting invariants; isolated Azure SQL qualification is separate.</summary>
public sealed class BudgetBridgeCompatibilityTests
{
    [Theory]
    [InlineData(38)]
    [InlineData(39)]
    public async Task OldStoresPreserveLiabilitiesAndOperateWithNullableEvidenceColumns(int version)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("budget-bridge"));
        var leases = new ControlLeaseStore(issues.Db);
        var budget = new DurableBudgetStore(issues.Db);
        await leases.InitializeDatasetAsync("dataset", 25m);
        static BudgetReservationRequest Request(string id, decimal amount) =>
            new("dataset", id, "forge", "run", "openai", "model", amount);
        Assert.True((await budget.ReserveAsync(Request("historic", 3m))).Admitted);
        Assert.True(await budget.CompleteAsync("dataset", "historic", 2m));
        Assert.True((await budget.ReserveAsync(Request("unknown", 4m))).Admitted);
        Assert.True(await budget.CompleteAsync("dataset", "unknown", null));

        if (version == 39)
        {
            await using var connection = await issues.Db.OpenAsync();
            using var upgrade = connection.CreateCommand();
            // Mirror M039's nullable additive shape without registering or shipping M039 in the bridge.
            upgrade.CommandText = """
                ALTER TABLE budget_reservation ADD COLUMN usage_json TEXT NULL;
                ALTER TABLE budget_reservation ADD COLUMN pricing_basis TEXT NULL;
                UPDATE budget_reservation SET usage_json='existing evidence', pricing_basis='existing basis'
                    WHERE request_id='historic';
                CREATE TABLE budget_reconciliation (
                    reconciliation_id TEXT NOT NULL PRIMARY KEY, dataset_id TEXT NOT NULL,
                    original_microusd INTEGER NOT NULL, reported_microusd INTEGER NOT NULL,
                    source TEXT NOT NULL, operator_name TEXT NOT NULL, evidence_sha256 TEXT NOT NULL,
                    manifest_json TEXT NOT NULL, created_at TEXT NOT NULL);
                CREATE TABLE budget_reconciliation_request (
                    request_id TEXT NOT NULL PRIMARY KEY, reconciliation_id TEXT NOT NULL,
                    dataset_id TEXT NOT NULL, original_microusd INTEGER NOT NULL);
                INSERT INTO budget_reconciliation VALUES
                    ('correction','dataset',2000000,10000,'operator-reported','fixture','hash','{}','fixture');
                INSERT INTO budget_reconciliation_request VALUES ('historic','correction','dataset',2000000);
                INSERT INTO schema_version(version,applied_at) VALUES(39,'fixture');
                """;
            await upgrade.ExecuteNonQueryAsync();
        }

        // The bridge ignores schema39 reconciliation credit. Rollback remains
        // conservative, retaining original liability instead of spending more.
        Assert.Equal(new BudgetSnapshot(25m, 2m, 4m), await budget.ReadAsync("dataset"));
        var replay = await budget.ReserveAsync(Request("historic", 3m));
        Assert.True(replay.Admitted);
        Assert.False(replay.IsNew);
        Assert.Equal(2m, replay.Reservation!.ActualUsd);
        Assert.True((await budget.ReserveAsync(Request("new", 1m))).Admitted);
        Assert.True(await budget.CompleteAsync("dataset", "new", 0.25m));
        Assert.True(await budget.CompleteAsync("dataset", "historic", 2m));
        Assert.False((await budget.ReserveAsync(Request("too-large", 19m))).Admitted);
        Assert.Equal(new BudgetSnapshot(25m, 2.25m, 4m), await budget.ReadAsync("dataset"));

        var lease = await leases.TryAcquireAsync("dataset", "bridge", "activation");
        Assert.NotNull(lease);
        Assert.True(await leases.IsOwnerAsync("dataset", "bridge", lease!.Epoch));
        Assert.True(await leases.ReleaseAsync("dataset", "bridge", lease.Epoch));
        Assert.Equal(38, IssueStore.CurrentSchemaVersion);
        Assert.Equal(38, SqlServerMigrations.ExpectedVersion);

        await using var check = await issues.Db.OpenAsync();
        using var command = check.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_version";
        Assert.Equal(version, Convert.ToInt32(await command.ExecuteScalarAsync()));
        if (version == 39)
        {
            command.CommandText = "SELECT reported_microusd FROM budget_reconciliation WHERE reconciliation_id='correction'";
            Assert.Equal(10000L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT original_microusd FROM budget_reconciliation_request WHERE request_id='historic'";
            Assert.Equal(2000000L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT usage_json,pricing_basis FROM budget_reservation WHERE request_id='historic'";
            using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("existing evidence", reader.GetString(0));
                Assert.Equal("existing basis", reader.GetString(1));
            }
            command.CommandText = "SELECT usage_json,pricing_basis FROM budget_reservation WHERE request_id='new'";
            using var added = await command.ExecuteReaderAsync();
            Assert.True(await added.ReadAsync());
            Assert.True(added.IsDBNull(0));
            Assert.True(added.IsDBNull(1));
        }
    }
}
