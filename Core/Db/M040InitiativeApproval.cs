using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>Durable proposal authority and project-local ingestion/refinement receipts.</summary>
public sealed class M040InitiativeApproval : ISqlServerMigration
{
    public int Version => 40;
    public string Name => "initiative-approval";
    public void Up(DbConnection connection, SqlServerDialect dialect, ForgeSchemaProfile profile) => Create(connection, dialect);
    public static void Create(DbConnection connection, ISqlDialect d)
    {
        string key = d.Provider == ForgeDbProvider.SqlServer ? "NVARCHAR(128)" : "TEXT";
        string text = d.Provider == ForgeDbProvider.SqlServer ? "NVARCHAR(MAX)" : "TEXT";
        string number = d.Provider == ForgeDbProvider.SqlServer ? "INT" : "INTEGER";
        var definitions = new Dictionary<string, string>
        {
            ["initiative_lock"] = $"id {number} NOT NULL PRIMARY KEY, value {number} NOT NULL",
            ["initiative_proposal"] = $"id {key} NOT NULL PRIMARY KEY, project_id {key} NOT NULL, source_key {key} NOT NULL, origin {key} NOT NULL, source_json {text} NOT NULL, session_id {key} NULL, current_revision {number} NOT NULL, created_at {key} NOT NULL, UNIQUE(project_id,source_key)",
            ["initiative_revision"] = $"proposal_id {key} NOT NULL, revision {number} NOT NULL, content_hash {key} NOT NULL, title {text} NOT NULL, body {text} NOT NULL, priority {number} NOT NULL, created_at {key} NOT NULL, approved_at {key} NULL, approved_by {key} NULL, epic_id {key} NULL, spec_id {key} NULL, PRIMARY KEY(proposal_id,revision)",
            ["initiative_ingestion"] = $"proposal_id {key} NOT NULL, revision {number} NOT NULL, project_id {key} NOT NULL, content_hash {key} NOT NULL, epic_id {key} NOT NULL, spec_id {key} NOT NULL, refinement_state {key} NOT NULL, attempts {number} NOT NULL, last_error {text} NULL, updated_at {key} NOT NULL, PRIMARY KEY(proposal_id,revision)"
        };
        foreach (var (table, columns) in definitions)
        {
            using var command = connection.CreateCommand();
            command.CommandText = d.Provider == ForgeDbProvider.SqlServer
                ? $"IF OBJECT_ID(N'{d.Table(table)}', N'U') IS NULL CREATE TABLE {d.Table(table)} ({columns});"
                : $"CREATE TABLE IF NOT EXISTS {d.Table(table)} ({columns});";
            command.ExecuteNonQuery();
        }
        using var seed = connection.CreateCommand();
        seed.CommandText = $"INSERT INTO {d.Table("initiative_lock")}(id,value) SELECT 1,0 WHERE NOT EXISTS(SELECT 1 FROM {d.Table("initiative_lock")} WHERE id=1)";
        seed.ExecuteNonQuery();
    }
}
