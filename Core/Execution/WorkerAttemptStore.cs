using System.Data;
using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core.Execution;

public sealed record WorkerAssignment(string Id, string DatasetId, string AdoptionKey, string ProjectId,
    string TaskId, string Role, string DispatchId, string SourceSha, string WorktreePath, string AssignmentJson,
    string AttemptToken, string State = "queued", string? PodUid = null, bool CancelRequested = false,
    string? ResultJson = null, string? Error = null);

/// <summary>Durable attempt ownership. A pod claim is single-use; running attempts are never automatically reclaimed.</summary>
public sealed class WorkerAttemptStore(IDbConnectionFactory db)
{
    private string Table => db.Dialect.Table("worker_attempt");
    public async Task<bool> HasActiveAsync(string datasetId, string projectId, string taskId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Table} WHERE dataset_id=@dataset AND project_id=@project AND task_id=@task AND state IN ('queued','running')";
        Add(command, "@dataset", datasetId); Add(command, "@project", projectId); Add(command, "@task", taskId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
    }

    public async Task<IReadOnlyList<string>> ListRoleAttemptIdsAsync(string datasetId, string projectId, string taskId, string role, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM {Table} WHERE dataset_id=@dataset AND project_id=@project AND task_id=@task AND role=@role";
        Add(command, "@dataset", datasetId); Add(command, "@project", projectId); Add(command, "@task", taskId); Add(command, "@role", role);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<WorkerAssignment> CreateOrGetAsync(WorkerAssignment assignment, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {Table} (id,dataset_id,adoption_key,project_id,task_id,role,dispatch_id,source_sha,
                worktree_path,assignment_json,attempt_token,state,created_at,updated_at)
            SELECT @id,@dataset,@key,@project,@task,@role,@dispatch,@sha,@path,@assignment,@token,'queued',@now,@now
            WHERE NOT EXISTS (SELECT 1 FROM {Table} WHERE dataset_id=@dataset AND adoption_key=@key);
            """;
        Add(command, "@id", assignment.Id); Add(command, "@dataset", assignment.DatasetId);
        Add(command, "@key", assignment.AdoptionKey); Add(command, "@project", assignment.ProjectId);
        Add(command, "@task", assignment.TaskId); Add(command, "@role", assignment.Role);
        Add(command, "@dispatch", assignment.DispatchId); Add(command, "@sha", assignment.SourceSha);
        Add(command, "@path", assignment.WorktreePath); Add(command, "@assignment", assignment.AssignmentJson);
        Add(command, "@token", assignment.AttemptToken); Add(command, "@now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(assignment.Id, ct) ?? throw new InvalidOperationException("Worker attempt insertion did not persist.");
    }

    public async Task<WorkerAssignment?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Table} WHERE id=@id";
        Add(command, "@id", id);
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return null;
        string Read(string name) => row.GetString(row.GetOrdinal(name));
        string? Maybe(string name) => row.IsDBNull(row.GetOrdinal(name)) ? null : Read(name);
        return new(Read("id"), Read("dataset_id"), Read("adoption_key"), Read("project_id"), Read("task_id"),
            Read("role"), Read("dispatch_id"), Read("source_sha"), Read("worktree_path"), Read("assignment_json"),
            Read("attempt_token"), Read("state"), Maybe("pod_uid"), Convert.ToInt32(row["cancel_requested"]) != 0,
            Maybe("result_json"), Maybe("error"));
    }

    public Task<bool> ClaimAsync(string id, string podUid, CancellationToken ct = default) =>
        UpdateAsync("state='running', pod_uid=@pod", "id=@id AND state='queued' AND cancel_requested=0", id, podUid, null, null, null, ct);

    public async Task EnsureOwnerAsync(string id, string podUid, string token, CancellationToken ct = default)
    {
        var row = await GetAsync(id, ct);
        if (row is null || row.State != "running" || row.PodUid != podUid || row.AttemptToken != token || row.CancelRequested)
            throw new OperationCanceledException("Worker attempt is cancelled or not owned by this pod.", ct);
    }

    public Task<bool> CompleteAsync(string id, string podUid, string token, string? resultJson, string? error,
        CancellationToken ct = default) => UpdateAsync(
            "state=CASE WHEN cancel_requested=1 THEN 'cancelled' WHEN @error IS NULL THEN 'succeeded' ELSE 'failed' END, result_json=@result, error=@error",
            "id=@id AND state='running' AND pod_uid=@pod AND attempt_token=@token", id, podUid, token, resultJson, error, ct);

    public Task<bool> MarkJobFailedAsync(string id, string error, CancellationToken ct = default) =>
        UpdateAsync("state='failed', error=@error", "id=@id AND state IN ('queued','running')",
            id, null, null, null, error, ct);

    public Task<bool> RequestCancellationAsync(string id, CancellationToken ct = default) =>
        UpdateAsync("cancel_requested=1, state=CASE WHEN state='queued' THEN 'cancelled' ELSE state END",
            "id=@id AND state IN ('queued','running')", id, null, null, null, null, ct);

    private async Task<bool> UpdateAsync(string set, string where, string id, string? pod, string? token,
        string? result, string? error, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {Table} SET {set}, updated_at=@now WHERE {where}";
        Add(command, "@id", id); Add(command, "@pod", pod); Add(command, "@token", token);
        Add(command, "@result", result); Add(command, "@error", error); Add(command, "@now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
}
