using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>Project-owned materializations of records written to legacy project stores.</summary>
public sealed class M043OwnedArtifactHistory : ISqlServerMigration
{
    public int Version => 43;
    public string Name => "owned-artifact-history";

    public void Up(DbConnection connection, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile == ForgeSchemaProfile.Workload) Create(connection, dialect);
    }

    public static void Create(DbConnection connection, ISqlDialect dialect)
    {
        var sqlServer = dialect.Provider == ForgeDbProvider.SqlServer;
        var key = sqlServer ? "NVARCHAR(128)" : "TEXT";
        var text = sqlServer ? "NVARCHAR(MAX)" : "TEXT";
        var table = dialect.Table("owned_artifact_history");
        using var command = connection.CreateCommand();
        var definition = $"""
            {table} (
                writer_project_id {key} NOT NULL,
                artifact_id {key} NOT NULL,
                spec_id {key} NOT NULL,
                artifact_type {key} NOT NULL,
                kind {key} NOT NULL,
                body_kind {key} NOT NULL,
                title {text} NOT NULL,
                record_json {text} NOT NULL,
                art_output_root {text} NULL,
                PRIMARY KEY(writer_project_id, artifact_id)
            );
            """;
        command.CommandText = sqlServer
            ? $"IF OBJECT_ID(N'{table}', N'U') IS NULL CREATE TABLE {definition}"
            : $"CREATE TABLE IF NOT EXISTS {definition}";
        command.ExecuteNonQuery();
        foreach (var (name, columns) in new[]
        {
            ("ix_owned_artifact_history_spec", "spec_id, artifact_type, artifact_id"),
            ("ix_owned_artifact_history_id", "artifact_id, artifact_type")
        })
        {
            command.CommandText = sqlServer
                ? $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'{name}' AND object_id=OBJECT_ID(N'{table}')) CREATE INDEX {name} ON {table}({columns});"
                : $"CREATE INDEX IF NOT EXISTS {name} ON {table}({columns});";
            command.ExecuteNonQuery();
        }
    }
}
