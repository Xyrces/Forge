using System.Data;
using System.Security.Cryptography;
using System.Text;
using Forge.Core.Db;

namespace Forge.Deploy;

public sealed partial class DeploymentStore
{
    public async Task<DeploymentCandidate> CreateOrGetRoutineAsync(string projectId, string completionId,
        string sourceSha, CancellationToken ct = default)
    {
        var id = "deploy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectId + "\0" + completionId)))[..32].ToLowerInvariant();
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using (var fence = conn.CreateCommand())
        {
            fence.Transaction = tx;
            fence.CommandText = $"UPDATE {T("completion_lock")} SET value=value WHERE id=1";
            if (await fence.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Completion lock unavailable.");
        }
        using (var bind = conn.CreateCommand())
        {
            bind.Transaction = tx;
            bind.CommandText = $"UPDATE {T("sprint_completion")} SET deployment_candidate_id=@candidate,updated_at=@now WHERE id=@completion AND project_id=@project AND source_sha=@source AND ready_for_deployment=1 AND abandonment_json IS NULL AND (deployment_candidate_id IS NULL OR deployment_candidate_id=@candidate)";
            bind.AddParam("@candidate", id); bind.AddParam("@completion", completionId);
            bind.AddParam("@project", projectId); bind.AddParam("@source", sourceSha);
            bind.AddParam("@now", DateTime.UtcNow.ToString(DateFormat));
            if (await bind.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Completion is no longer authorized for these release inputs.");
        }
        DeploymentCandidate? prior = null;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = SelectColumns + " WHERE id=@id";
            find.AddParam("@id", id);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var existing = Read(reader);
                if (existing.ProjectId != projectId || existing.CompletionId != completionId || existing.CommitSha != sourceSha)
                    throw new InvalidOperationException("Completion already binds different deployment inputs.");
                prior = existing;
            }
        }
        if (prior is not null)
        {
            await tx.CommitAsync(ct);
            return prior;
        }
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = $"INSERT INTO {T("deployment")}(id,project_id,commit_sha,status,requested_at,requested_by,completion_id) VALUES(@id,@project,@sha,'Pending',@now,'approved initiative routine policy',@completion)";
            insert.AddParam("@id", id); insert.AddParam("@project", projectId); insert.AddParam("@sha", sourceSha);
            insert.AddParam("@now", DateTime.UtcNow.ToString(DateFormat)); insert.AddParam("@completion", completionId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<IReadOnlyList<DeploymentCandidate>> ListPendingAsync(string projectId, string? afterId = null,
        int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumnsTop + " WHERE project_id=@project AND status IN ('Pending','BuildPassed','Approved','Deploying')"
            + (afterId is null ? "" : " AND id>@after") + " ORDER BY id" + _db.Dialect.LimitParam("@limit");
        cmd.AddParam("@project", projectId); cmd.AddParam("@limit", Math.Clamp(limit, 1, 100));
        if (afterId is not null) cmd.AddParam("@after", afterId);
        var result = new List<DeploymentCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    public async Task PinReceiptAsync(string id, string receiptJson, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {T("deployment")} SET receipt_json=@receipt,status='BuildPassed',build_log='Trusted exact-source CI receipt verified' WHERE id=@id AND completion_id IS NOT NULL AND receipt_json IS NULL AND status='Pending'";
        cmd.AddParam("@receipt", receiptJson); cmd.AddParam("@id", id);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0 && (await GetAsync(id, ct))?.ReceiptJson != receiptJson)
            throw new InvalidOperationException("Deployment receipt is immutable or candidate is no longer awaiting qualification.");
    }

    public Task RecordQualificationWaitAsync(string id, string note, CancellationToken ct = default) =>
        ExecAsync($"UPDATE {T("deployment")} SET build_log=@note WHERE id=@id AND status='Pending'",
            cmd => { cmd.AddParam("@id", id); cmd.AddParam("@note", note); }, ct);

    public Task MarkIndependentProgressAsync(string id, DeploymentExecutionResult progress, CancellationToken ct = default) =>
        progress.AdmissionHeld ? Task.CompletedTask : progress.StillInProgress
            ? ExecAsync($"UPDATE {T("deployment")} SET status='Deploying' WHERE id=@id AND status='Approved'",
                cmd => cmd.AddParam("@id", id), ct)
            : ExecAsync($"UPDATE {T("deployment")} SET status=@status,deploy_log=@log,deployed_at=@at WHERE id=@id AND status IN ('Approved','Deploying')",
                cmd => { cmd.AddParam("@id", id); cmd.AddParam("@status", progress.Success ? "Deployed" : "DeployFailed");
                    cmd.AddParam("@log", progress.Log); cmd.AddParam("@at", progress.Success ? DateTime.UtcNow.ToString(DateFormat) : DBNull.Value); }, ct);
}
