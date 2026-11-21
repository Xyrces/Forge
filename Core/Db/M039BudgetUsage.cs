using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>Additive billing evidence; existing reservations and settlements remain untouched.</summary>
public sealed class M039BudgetUsage : ISqlServerMigration
{
    public int Version => 39;
    public string Name => "budget-usage-evidence";
    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Core) return;
        using var command = conn.CreateCommand();
        command.CommandText = SqlServerDdl(dialect.Qualifier) + Control.ControlSchema.ReconciliationDdl(dialect);
        command.ExecuteNonQuery();
    }
    public static string SqlServerDdl(string schema) => $"""
        IF COL_LENGTH(N'[{schema}].[budget_reservation]', N'usage_json') IS NULL
            ALTER TABLE [{schema}].[budget_reservation] ADD usage_json NVARCHAR(MAX) NULL;
        IF COL_LENGTH(N'[{schema}].[budget_reservation]', N'pricing_basis') IS NULL
            ALTER TABLE [{schema}].[budget_reservation] ADD pricing_basis NVARCHAR(128) NULL;
        """;
    public static void ApplySqlite(DbConnection connection)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "PRAGMA table_info(budget_reservation)";
            using var reader = query.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        foreach (var column in new[] { "usage_json", "pricing_basis" })
        {
            if (columns.Contains(column)) continue;
            using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE budget_reservation ADD COLUMN {column} TEXT NULL";
            command.ExecuteNonQuery();
        }
        using var reconciliation = connection.CreateCommand();
        reconciliation.CommandText = Control.ControlSchema.ReconciliationDdl(SqliteDialect.Instance);
        reconciliation.ExecuteNonQuery();
    }
}
