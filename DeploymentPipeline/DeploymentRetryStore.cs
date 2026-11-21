using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Configuration;
using Forge.Core.Completion;
using Forge.Core.Db;

namespace Forge.Deploy;

public sealed record RetryReleaseRequest(string ExpectedSourceSha, string ExpectedImage);

public sealed partial class DeploymentStore
{
    // The HTTP boundary verifies that the old Kubernetes operation has stopped.
    // This transaction preserves its evidence and authorizes only a new attempt of the same inputs.
    internal async Task<DeploymentCandidate> RetryRoutineAsync(ProjectOptions project, DeploymentCandidate observed,
        RetryReleaseRequest request, string actor, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAdmissionAsync(connection, tx, ct);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = SelectColumns + " WHERE id=@old AND project_id=@project";
        command.AddParam("@old", observed.Id); command.AddParam("@project", project.Id);
        DeploymentCandidate previous;
        await using (var reader = await command.ExecuteReaderAsync(ct))
            previous = await reader.ReadAsync(ct) ? Read(reader) : throw new InvalidOperationException("The failed deployment no longer exists.");
        if (previous.Status is not (DeploymentStatus.DeployFailed or DeploymentStatus.Rejected)
            || previous.CompletionId is null || previous.EnvironmentName is null || previous.TargetJson is null
            || previous.SourceCandidateId is not null || previous != observed)
            throw new InvalidOperationException("The deployment changed. Refresh before retrying.");
        var receipt = JsonSerializer.Deserialize<TrustedImageReceipt>(previous.ReceiptJson ?? "null", EnvironmentJson)
            ?? throw new InvalidOperationException("A retry requires the original verified image receipt.");
        if (receipt.SourceSha != request.ExpectedSourceSha || receipt.ImageDigest != request.ExpectedImage)
            throw new InvalidOperationException("The selected release differs from the confirmed retry.");
        KubernetesDeploymentExecutor.ResolveTrustedProfile(KubernetesDeploymentExecutor.ApplyTarget(project, previous), previous);
        command.CommandText = $"SELECT deployment_candidate_id,completion_policy_json,completion_gate_approval_json FROM {T("sprint_completion")} WHERE id=@completion AND project_id=@project AND source_sha=@source AND ready_for_deployment=1 AND delivery_required=1 AND outcome='PendingEvidence' AND abandonment_json IS NULL";
        command.AddParam("@completion", previous.CompletionId); command.AddParam("@source", previous.CommitSha);
        string current;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.IsDBNull(1))
                throw new InvalidOperationException("The sprint no longer authorizes this delivery.");
            current = reader.GetString(0);
            var policy = JsonSerializer.Deserialize<CompletionPolicySnapshot>(reader.GetString(1));
            if (policy is null || policy.TargetJson != previous.TargetJson || policy.EnvironmentName != previous.EnvironmentName
                || policy.RequireApproval && reader.IsDBNull(2))
                throw new InvalidOperationException("The retry requires the original target and explicit approvals.");
        }
        if (current != previous.Id)
        {
            command.CommandText = SelectColumns + " WHERE id=@current";
            command.AddParam("@current", current);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("The current deployment is unavailable.");
            var retry = Read(reader);
            if (retry.RetryOfCandidateId != previous.Id)
                throw new InvalidOperationException("The sprint has advanced to another deployment attempt.");
            return retry;
        }
        var id = "deploy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project.Id + "\0retry\0" + previous.Id)))[..32].ToLowerInvariant();
        command.CommandText = $"INSERT INTO {T("deployment")}(id,project_id,commit_sha,commit_summary,status,requested_at,requested_by,build_log,completion_id,receipt_json,environment_name,target_json,retry_of_candidate_id) VALUES(@new,@project,@source,@summary,'BuildPassed',@now,@actor,'Retry of the same verified source and image',@completion,@receipt,@env,@target,@old)";
        command.AddParam("@new", id); command.AddParam("@summary", previous.CommitSummary);
        command.AddParam("@now", DateTime.UtcNow.ToString(DateFormat)); command.AddParam("@actor", actor);
        command.AddParam("@receipt", previous.ReceiptJson); command.AddParam("@env", previous.EnvironmentName);
        command.AddParam("@target", previous.TargetJson);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = $"UPDATE {T("sprint_completion")} SET deployment_candidate_id=@new,delivery_note='Retry awaiting deployment admission',updated_at=@now WHERE id=@completion AND deployment_candidate_id=@old";
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The sprint deployment changed.");
        await tx.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }
}
