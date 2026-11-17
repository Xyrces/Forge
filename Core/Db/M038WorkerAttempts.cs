using System.Data.Common;

namespace Forge.Core.Db;

public sealed class M038WorkerAttempts : ISqlServerMigration
{
    public int Version => 38;
    public string Name => "worker-attempts";
    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Core) return;
        using var command = conn.CreateCommand();
        command.CommandText = SqlServerDdl(dialect.Qualifier);
        command.ExecuteNonQuery();
    }

    public const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS worker_attempt (
            id TEXT PRIMARY KEY, dataset_id TEXT NOT NULL, adoption_key TEXT NOT NULL,
            project_id TEXT NOT NULL, task_id TEXT NOT NULL, role TEXT NOT NULL,
            dispatch_id TEXT NOT NULL, source_sha TEXT NOT NULL, worktree_path TEXT NOT NULL,
            assignment_json TEXT NOT NULL, attempt_token TEXT NOT NULL,
            state TEXT NOT NULL, pod_uid TEXT NULL, cancel_requested INTEGER NOT NULL DEFAULT 0,
            result_json TEXT NULL, error TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            UNIQUE(dataset_id, adoption_key));
        """;

    public static string SqlServerDdl(string qualifier) => $"""
        IF OBJECT_ID(N'[{qualifier}].[worker_attempt]', N'U') IS NULL
        CREATE TABLE [{qualifier}].[worker_attempt] (
            id NVARCHAR(64) NOT NULL PRIMARY KEY, dataset_id NVARCHAR(128) NOT NULL,
            adoption_key NVARCHAR(64) NOT NULL, project_id NVARCHAR(128) NOT NULL,
            task_id NVARCHAR(128) NOT NULL, role NVARCHAR(64) NOT NULL,
            dispatch_id NVARCHAR(128) NOT NULL, source_sha NVARCHAR(64) NOT NULL,
            worktree_path NVARCHAR(2048) NOT NULL, assignment_json NVARCHAR(MAX) NOT NULL,
            attempt_token NVARCHAR(64) NOT NULL, state NVARCHAR(32) NOT NULL,
            pod_uid NVARCHAR(128) NULL, cancel_requested INT NOT NULL DEFAULT 0,
            result_json NVARCHAR(MAX) NULL, error NVARCHAR(MAX) NULL,
            created_at NVARCHAR(40) NOT NULL, updated_at NVARCHAR(40) NOT NULL,
            CONSTRAINT UQ_worker_attempt_adoption UNIQUE(dataset_id, adoption_key));
        """;
}
