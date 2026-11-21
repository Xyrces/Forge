using Forge.Core;
using Forge.Core.Db;
using Xunit;

namespace Forge.Tests;

public sealed class ImplementationFormatSchemaTests
{
    [Fact]
    public async Task ExplicitInitializationAdvances41WithoutChangingStoredWorkOrTableShape()
    {
        var path = TempRoot.Instance.NewDbPath("implementation-format");
        string before;
        await using (var issues = new IssueStore(path, "project"))
        {
            issues.EnsureSchema();
            await using var connection = await issues.Db.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO memory(key,body,ts) VALUES ('format-fixture','unchanged','2026-11-17'); DELETE FROM schema_version WHERE version=42; INSERT OR IGNORE INTO schema_version(version,applied_at) VALUES (41,'fixture')";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name)";
            before = (string)(await command.ExecuteScalarAsync())!;
        }
        await using var reopened = new IssueStore(path, "project");
        reopened.EnsureSchema();
        await using var check = await reopened.Db.OpenAsync();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT MAX(version) FROM schema_version";
        Assert.Equal((long)IssueStore.CurrentSchemaVersion, Convert.ToInt64(await read.ExecuteScalarAsync()));
        read.CommandText = "SELECT body FROM memory WHERE key='format-fixture'";
        Assert.Equal("unchanged", await read.ExecuteScalarAsync());
        read.CommandText = "SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name)";
        Assert.Equal(before, await read.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(ForgeSchemaProfile.Core)]
    [InlineData(ForgeSchemaProfile.Workload)]
    public void SqlMigrationIsRegisteredAndDoesNotReadOrWriteEitherProfile(ForgeSchemaProfile profile)
    {
        var migration = Assert.IsType<M042ImplementationEvidence>(SqlServerMigrations.All.Single(m => m.Version == 42));
        // A closed connection would reject SQL execution: this step only advances
        // the version through the existing runner, never creates or rewrites data.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        migration.Up(connection, new SqlServerDialect("audit"), profile);
        migration.Up(connection, new SqlServerDialect("audit"), profile);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }
}
