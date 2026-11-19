using System.Data.Common;

namespace Forge.Core.Db;

public sealed class M044DeploymentEnvironments : ISqlServerMigration
{
    public int Version => 44;
    public string Name => "project-deployment-environments";

    public void Up(DbConnection connection, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile == ForgeSchemaProfile.Workload) Create(connection, dialect);
    }

    public static void Create(DbConnection connection, ISqlDialect dialect)
    {
        var sql = dialect.Provider == ForgeDbProvider.SqlServer;
        var key = sql ? "NVARCHAR(128)" : "TEXT";
        var text = sql ? "NVARCHAR(MAX)" : "TEXT";
        using var cmd = connection.CreateCommand();
        var settings = dialect.Table("deployment_environment_policy");
        var definition = $"{settings}(project_id {key} NOT NULL PRIMARY KEY,revision INT NOT NULL,policy_json {text} NOT NULL,updated_by {key} NOT NULL,updated_at {key} NOT NULL)";
        cmd.CommandText = sql ? $"IF OBJECT_ID(N'{settings}',N'U') IS NULL CREATE TABLE {definition}" : $"CREATE TABLE IF NOT EXISTS {definition}";
        cmd.ExecuteNonQuery();
        foreach (var (name, column, type) in new[] {
            ("sprint", "completion_policy_json", text), ("deployment", "environment_name", key), ("deployment", "target_json", text),
            ("deployment", "source_candidate_id", key), ("deployment", "retry_of_candidate_id", key), ("sprint_completion", "completion_policy_json", text),
            ("sprint_completion", "completion_gate_approval_json", text), ("sprint_completion", "reconciliation_json", text) })
        {
            var table = dialect.Table(name);
            if (sql)
                cmd.CommandText = $"IF COL_LENGTH(N'{table}',N'{column}') IS NULL ALTER TABLE {table} ADD {column} {type} NULL";
            else
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{name}') WHERE name='{column}'";
                if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) continue;
                cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type} NULL";
            }
            cmd.ExecuteNonQuery();
        }
        var deployment = dialect.Table("deployment");
        // Install the replacement before retiring the old stricter index. Repeated
        // initialization must not remove the live uniqueness guarantee.
        cmd.CommandText = sql
            ? $"IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'{deployment}') AND name='ux_deployment_active_completion') CREATE UNIQUE INDEX ux_deployment_active_completion ON {deployment}(completion_id) WHERE completion_id IS NOT NULL AND status <> 'DeployFailed' AND status <> 'Rejected';"
            : $"CREATE UNIQUE INDEX IF NOT EXISTS ux_deployment_active_completion ON {deployment}(completion_id) WHERE completion_id IS NOT NULL AND status <> 'DeployFailed' AND status <> 'Rejected';";
        cmd.ExecuteNonQuery();
        cmd.CommandText = sql
            ? $"IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'{deployment}') AND name='ux_deployment_completion') DROP INDEX ux_deployment_completion ON {deployment};"
            : "DROP INDEX IF EXISTS ux_deployment_completion;";
        cmd.ExecuteNonQuery();
    }
}
