using Forge.Configuration;
using Forge.Core;
using Forge.Core.Db;
using Forge.Orchestrator.Control;
using Xunit;

namespace Forge.Tests;

public sealed class SchemaUpgradePreflightTests
{
    [Fact]
    public async Task WorkerPreflightUsesActualWorkerSchemaAndCountsOnlyLiveAttempts()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("migration-workers"));
        await using var connection = await issues.Db.OpenAsync();
        foreach (var state in new[] { "queued", "running", "completed", "failed" })
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO worker_attempt (id,dataset_id,adoption_key,project_id,task_id,role,dispatch_id,source_sha,worktree_path,assignment_json,attempt_token,state,created_at,updated_at) VALUES (@state,'dataset',@state,'forge','task','CoreDev','dispatch','source','/work','{}','token',@state,'now','now')";
            var parameter = insert.CreateParameter(); parameter.ParameterName = "@state"; parameter.Value = state;
            insert.Parameters.Add(parameter);
            await insert.ExecuteNonQueryAsync();
        }
        await using var query = connection.CreateCommand();
        query.CommandText = ControllerInitializer.ActiveWorkerAttemptsQuery(issues.Db.Dialect);
        Assert.Equal(2L, Convert.ToInt64(await query.ExecuteScalarAsync()));
        Assert.Contains("[core].[worker_attempt]", ControllerInitializer.ActiveWorkerAttemptsQuery(new SqlServerDialect("core")));
    }

    [Fact]
    public void FreshInitializationHasNoUpgradeContract()
        => ControllerInitializer.ValidateMigrationContract(new BootstrapOptions());

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UpgradeAcceptsBothSourceAndTargetForPartialAdditiveResume(bool coreTarget, bool workloadTarget)
    {
        var target = SqlServerMigrations.ExpectedVersion;
        var options = new BootstrapOptions { MigrationFromVersion = target - 1, MigrationToVersion = target };
        ControllerInitializer.ValidateMigrationVersions(options, coreTarget ? target : target - 1, workloadTarget ? target : target - 1);
    }

    [Fact]
    public void WrongBinaryTargetAndPartialContractsAreRejected()
    {
        var target = SqlServerMigrations.ExpectedVersion;
        foreach (var options in new[] {
            new BootstrapOptions { MigrationFromVersion = target - 1 },
            new BootstrapOptions { MigrationToVersion = target },
            new BootstrapOptions { MigrationFromVersion = target, MigrationToVersion = target },
            new BootstrapOptions { MigrationFromVersion = target - 1, MigrationToVersion = target + 1 } })
            Assert.Throws<InvalidOperationException>(() => ControllerInitializer.ValidateMigrationContract(options));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    public void RegistryAndWorkloadMustBothBeWithinExactTransition(int coreOffset, int workloadOffset)
    {
        var target = SqlServerMigrations.ExpectedVersion;
        var options = new BootstrapOptions { MigrationFromVersion = target - 1, MigrationToVersion = target };
        Assert.Throws<InvalidOperationException>(() => ControllerInitializer.ValidateMigrationVersions(options,
            target - 2 + coreOffset, target + workloadOffset));
    }
}
