using System.Data.Common;

namespace Forge.Core.Db;

public sealed class M041SprintCompletion : ISqlServerMigration
{
    public int Version => 41;
    public string Name => "sprint-completion";
    public void Up(DbConnection connection, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile == ForgeSchemaProfile.Workload) Create(connection, dialect);
    }

    public static void Create(DbConnection connection, ISqlDialect d)
    {
        var sqlServer = d.Provider == ForgeDbProvider.SqlServer;
        var key = sqlServer ? "NVARCHAR(128)" : "TEXT";
        var text = sqlServer ? "NVARCHAR(MAX)" : "TEXT";
        var integer = sqlServer ? "BIGINT" : "INTEGER";
        var tables = new Dictionary<string, string>
        {
            ["completion_lock"] = "id INT NOT NULL PRIMARY KEY,value INT NOT NULL",
            ["sprint_completion"] = $"id {key} NOT NULL PRIMARY KEY,project_id {key} NOT NULL,sprint_id {key} NOT NULL UNIQUE,plan_audit_id {integer} NULL,plan_hash {key} NULL,outcome {key} NOT NULL,members_json {text} NOT NULL,evidence_json {text} NOT NULL,bookkeeping_state {key} NOT NULL,source_sha {key} NULL,deployment_candidate_id {key} NULL,created_at {key} NOT NULL,updated_at {key} NOT NULL,ready_for_deployment INT NOT NULL,delivery_note {text} NULL",
            ["completion_triage"] = $"id {key} NOT NULL PRIMARY KEY,completion_id {key} NOT NULL,state {key} NOT NULL,sources_json {text} NOT NULL,decision_json {text} NULL,error {text} NULL,updated_at {key} NOT NULL",
            ["completion_draft_claim"] = $"draft_id {integer} NOT NULL PRIMARY KEY,batch_id {key} NOT NULL,item_key {key} NULL,target_id {key} NULL"
        };
        foreach (var (name, columns) in tables)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sqlServer
                ? $"IF OBJECT_ID(N'{d.Table(name)}',N'U') IS NULL CREATE TABLE {d.Table(name)}({columns});"
                : $"CREATE TABLE IF NOT EXISTS {d.Table(name)}({columns});";
            cmd.ExecuteNonQuery();
        }
        using (var sourceEvidence = connection.CreateCommand())
        {
            sourceEvidence.CommandText = sqlServer
                ? $"IF COL_LENGTH(N'{d.Table("sprint_completion")}',N'source_evidence_json') IS NULL ALTER TABLE {d.Table("sprint_completion")} ADD source_evidence_json {text} NULL;"
                : $"ALTER TABLE {d.Table("sprint_completion")} ADD COLUMN source_evidence_json {text} NULL;";
            if (sqlServer) sourceEvidence.ExecuteNonQuery();
            else
            {
                using var columns = connection.CreateCommand(); columns.CommandText = "PRAGMA table_info(sprint_completion)";
                using var reader = columns.ExecuteReader(); var exists = false;
                while (reader.Read()) if (reader.GetString(1) == "source_evidence_json") exists = true;
                reader.Close();
                if (!exists) sourceEvidence.ExecuteNonQuery();
            }
        }
        using var seed = connection.CreateCommand();
        seed.CommandText = $"INSERT INTO {d.Table("completion_lock")}(id,value) SELECT 1,0 WHERE NOT EXISTS(SELECT 1 FROM {d.Table("completion_lock")} WHERE id=1)";
        seed.ExecuteNonQuery();
        foreach (var column in new[] { "completion_id", "receipt_json" })
        {
            using var alter = connection.CreateCommand();
            if (!sqlServer)
            {
                using var columns = connection.CreateCommand();
                columns.CommandText = "PRAGMA table_info(deployment)";
                using var reader = columns.ExecuteReader();
                var exists = false;
                while (reader.Read()) if (reader.GetString(1) == column) exists = true;
                if (exists) continue;
            }
            var type = column == "completion_id" ? key : text;
            alter.CommandText = sqlServer
                ? $"IF COL_LENGTH(N'{d.Table("deployment")}',N'{column}') IS NULL ALTER TABLE {d.Table("deployment")} ADD {column} {type} NULL;"
                : $"ALTER TABLE deployment ADD COLUMN {column} {type} NULL;";
            alter.ExecuteNonQuery();
        }
        using var index = connection.CreateCommand();
        index.CommandText = sqlServer
            ? $"IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'{d.Table("deployment")}') AND name='ux_deployment_completion') CREATE UNIQUE INDEX ux_deployment_completion ON {d.Table("deployment")}(completion_id) WHERE completion_id IS NOT NULL;"
            : "CREATE UNIQUE INDEX IF NOT EXISTS ux_deployment_completion ON deployment(completion_id) WHERE completion_id IS NOT NULL;";
        index.ExecuteNonQuery();
    }
}
