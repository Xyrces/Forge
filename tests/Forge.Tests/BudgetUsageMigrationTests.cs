using Forge.Core.Db;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Tests;

public sealed class BudgetUsageMigrationTests
{
    [Fact]
    public void UpgradeKeepsOldLiabilityAndSupportsOldInsertShape()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE budget_reservation(request_id TEXT PRIMARY KEY, actual_microusd INTEGER); INSERT INTO budget_reservation VALUES('old',24809140)";
        command.ExecuteNonQuery();
        M039BudgetUsage.ApplySqlite(connection);
        M039BudgetUsage.ApplySqlite(connection);
        command.CommandText = "INSERT INTO budget_reservation(request_id,actual_microusd) VALUES('old-writer',100); SELECT actual_microusd,usage_json,pricing_basis FROM budget_reservation WHERE request_id='old'";
        using var reader = command.ExecuteReader(); Assert.True(reader.Read());
        Assert.Equal(24809140, reader.GetInt64(0)); Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public void SqlServerMigrationIsAdditiveAndGuarded()
    {
        var ddl = M039BudgetUsage.SqlServerDdl("core");
        Assert.Contains("COL_LENGTH", ddl); Assert.Contains("ADD usage_json NVARCHAR(MAX) NULL", ddl);
        Assert.Contains("ADD pricing_basis NVARCHAR(128) NULL", ddl);
        Assert.DoesNotContain("UPDATE", ddl); Assert.DoesNotContain("DELETE", ddl);
        Assert.Equal(39, SqlServerMigrations.ExpectedVersion);
    }
}
