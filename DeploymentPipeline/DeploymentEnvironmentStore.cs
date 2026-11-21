using System.Data;
using System.Data.Common;
using System.Text.Json;
using Forge.Configuration;
using Forge.Core.Db;

namespace Forge.Deploy;

public sealed record EnvironmentPolicyRow(string Name, string DisplayName, string KubernetesNamespace,
    string KubernetesDatasetId, List<string> PromoteTo, bool RequireApproval);
public sealed record ProjectEnvironmentPolicy(int Revision, string? SprintEnvironment, List<EnvironmentPolicyRow> Environments);
public sealed record PromoteReleaseRequest(string TargetEnvironment, string ExpectedSourceSha, string ExpectedImage);

public sealed partial class DeploymentStore
{
    internal static readonly JsonSerializerOptions EnvironmentJson = new(JsonSerializerDefaults.Web);

    private async Task LockAdmissionAsync(DbConnection conn, DbTransaction tx, CancellationToken ct)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"UPDATE {T("completion_lock")} SET value=value WHERE id=1";
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Deployment admission lock is unavailable.");
    }

    private static ProjectEnvironmentPolicy Describe(ProjectOptions project, int revision) => new(revision,
        project.Deployment?.SprintEnvironment, project.Deployment?.Environments.Select(e =>
            new EnvironmentPolicyRow(e.Key, e.Value.DisplayName, e.Value.KubernetesNamespace, e.Value.KubernetesDatasetId,
                e.Value.PromoteTo.ToList(), e.Value.RequireApproval)).ToList() ?? []);

    private static ProjectOptions ApplyPolicy(ProjectOptions project, ProjectEnvironmentPolicy policy)
    {
        var host = project.Deployment ?? throw new InvalidOperationException("This project has no configured deployment targets.");
        if (policy.Environments is null || policy.Environments.Count != host.Environments.Count)
            throw new InvalidOperationException("The environment list must match the project's provisioned targets.");
        var overrides = new Dictionary<string, DeploymentEnvironmentOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in policy.Environments)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Name) || !host.Environments.TryGetValue(row.Name, out var binding)
                || binding.KubernetesNamespace != row.KubernetesNamespace || binding.KubernetesDatasetId != row.KubernetesDatasetId
                || row.PromoteTo is null || !overrides.TryAdd(row.Name, binding with {
                    DisplayName = row.DisplayName, RequireApproval = row.RequireApproval, PromoteTo = row.PromoteTo }))
                throw new InvalidOperationException("A physical target changed. Reload the project before editing its policy.");
        }
        var effective = project with { Deployment = host with { SprintEnvironment = policy.SprintEnvironment, Environments = overrides } };
        DeploymentEnvironmentResolver.Validate(effective);
        return effective;
    }

    private async Task<ProjectEnvironmentPolicy> ReadPolicyAsync(ProjectOptions project, DbConnection conn, DbTransaction? tx,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT revision,policy_json FROM {T("deployment_environment_policy")} WHERE project_id=@id";
        cmd.AddParam("@id", project.Id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Describe(project, 0);
        var stored = JsonSerializer.Deserialize<ProjectEnvironmentPolicy>(reader.GetString(1), EnvironmentJson)
            ?? throw new InvalidOperationException("Stored environment policy is unreadable.");
        // Hosts can provision or retire physical targets between policy edits. Carry an override
        // forward only when the same target still owns the name; new bindings start with host policy.
        var available = Describe(project, reader.GetInt32(0));
        var retained = stored.Environments.Where(prior => available.Environments.Any(current => current.Name == prior.Name
            && current.KubernetesNamespace == prior.KubernetesNamespace && current.KubernetesDatasetId == prior.KubernetesDatasetId))
            .ToDictionary(e => e.Name, StringComparer.Ordinal);
        var rows = available.Environments.Select(current => retained.TryGetValue(current.Name, out var prior)
            ? prior with { PromoteTo = prior.PromoteTo.Where(retained.ContainsKey).ToList() } : current).ToList();
        var selected = stored.SprintEnvironment is { } name && retained.ContainsKey(name) ? name : null;
        return available with { SprintEnvironment = selected, Environments = rows };
    }

    public async Task<ProjectEnvironmentPolicy> GetEnvironmentPolicyAsync(ProjectOptions project, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await ReadPolicyAsync(project, conn, null, ct);
    }

    public async Task<ProjectOptions> GetEffectiveProjectAsync(ProjectOptions project, CancellationToken ct)
    {
        if (project.Deployment is null) return project;
        var policy = await GetEnvironmentPolicyAsync(project, ct);
        return ApplyPolicy(project, policy);
    }

    public async Task<ProjectEnvironmentPolicy> SaveEnvironmentPolicyAsync(ProjectOptions project, ProjectEnvironmentPolicy policy,
        string actor, CancellationToken ct)
    {
        var effective = ApplyPolicy(project, policy);
        var next = Describe(effective, checked(policy.Revision + 1));
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAdmissionAsync(conn, tx, ct);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var table = T("deployment_environment_policy");
        cmd.CommandText = policy.Revision == 0
            ? $"INSERT INTO {table}(project_id,revision,policy_json,updated_by,updated_at) SELECT @id,@rev,@json,@actor,@now WHERE NOT EXISTS (SELECT 1 FROM {table} WHERE project_id=@id)"
            : $"UPDATE {table} SET revision=@rev,policy_json=@json,updated_by=@actor,updated_at=@now WHERE project_id=@id AND revision=@prior";
        cmd.AddParam("@id", project.Id); cmd.AddParam("@rev", next.Revision); cmd.AddParam("@prior", policy.Revision);
        cmd.AddParam("@json", JsonSerializer.Serialize(next, EnvironmentJson)); cmd.AddParam("@actor", actor);
        cmd.AddParam("@now", DateTime.UtcNow.ToString(DateFormat));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Environment policy changed. Reload before saving.");
        await tx.CommitAsync(ct);
        return next;
    }

    internal async Task<DeploymentCandidate> RecordVerifiedInstallationAsync(DeploymentCandidate candidate, string journalEvidence, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAdmissionAsync(conn, tx, ct);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var table = T("deployment");
        cmd.CommandText = $"INSERT INTO {table}(id,project_id,commit_sha,commit_summary,status,requested_at,requested_by,deployed_at,deploy_log,receipt_json,environment_name,target_json) SELECT @id,@project,@sha,@summary,'Deployed',@now,@actor,@now,@evidence,@receipt,@env,@target WHERE NOT EXISTS(SELECT 1 FROM {table} WHERE id=@id)";
        cmd.AddParam("@id", candidate.Id); cmd.AddParam("@project", candidate.ProjectId); cmd.AddParam("@sha", candidate.CommitSha);
        cmd.AddParam("@summary", candidate.CommitSummary); cmd.AddParam("@now", DateTime.UtcNow.ToString(DateFormat));
        cmd.AddParam("@actor", candidate.RequestedBy); cmd.AddParam("@evidence", journalEvidence); cmd.AddParam("@receipt", candidate.ReceiptJson);
        cmd.AddParam("@env", candidate.EnvironmentName); cmd.AddParam("@target", candidate.TargetJson);
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = SelectColumns + " WHERE id=@id";
        DeploymentCandidate saved;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Installer release was not recorded.");
            saved = Read(reader);
        }
        if (saved.Status != DeploymentStatus.Deployed || saved.ProjectId != candidate.ProjectId || saved.CommitSha != candidate.CommitSha
            || saved.TargetJson != candidate.TargetJson || saved.ReceiptJson != candidate.ReceiptJson)
            throw new InvalidOperationException("The installer operation already belongs to another release.");
        await tx.CommitAsync(ct);
        return saved;
    }

    public async Task<DeploymentCandidate> PromoteAsync(ProjectOptions project, string sourceId, PromoteReleaseRequest request,
        string actor, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAdmissionAsync(conn, tx, ct);
        var policy = await ReadPolicyAsync(project, conn, tx, ct);
        var effective = ApplyPolicy(project, policy);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SelectColumns + " WHERE id=@source AND project_id=@project";
        cmd.AddParam("@source", sourceId); cmd.AddParam("@project", project.Id);
        DeploymentCandidate source;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            source = await reader.ReadAsync(ct) ? Read(reader) : throw new KeyNotFoundException("Release not found in this project.");
        if (source.Status != DeploymentStatus.Deployed || source.EnvironmentName is null || source.TargetJson is null)
            throw new InvalidOperationException("Only a verified environment release can be promoted.");
        if (KubernetesDeploymentExecutor.WithPromotionEligibility(effective, source).PromotionUnavailableReason is { } reason)
            throw new InvalidOperationException(reason);
        var receipt = JsonSerializer.Deserialize<TrustedImageReceipt>(source.ReceiptJson!, EnvironmentJson)!;
        if (receipt.SourceSha != request.ExpectedSourceSha || receipt.ImageDigest != request.ExpectedImage)
            throw new InvalidOperationException("The selection does not match this release's verified source and image. Refresh its details.");
        if (!effective.Deployment!.Environments.TryGetValue(source.EnvironmentName, out var origin)
            || !origin.PromoteTo.Contains(request.TargetEnvironment, StringComparer.Ordinal))
            throw new InvalidOperationException("This promotion route is not enabled for the source environment.");
        var target = DeploymentEnvironmentResolver.Capture(effective, request.TargetEnvironment);
        // Serialize target admission across duplicate clicks and competing promotions. A later
        // deployment permits an intentional re-promotion of a previously deployed version.
        cmd.CommandText = SelectColumnsTop + $" WHERE project_id=@project AND environment_name=@target AND status IN ('Approved','Deploying','Deployed','DeployFailed') ORDER BY CASE WHEN status IN ('Approved','Deploying') THEN 0 ELSE 1 END,requested_at DESC,id DESC{_db.Dialect.LimitParam("@limit")}";
        cmd.AddParam("@target", target.Name); cmd.AddParam("@limit", 1);
        DeploymentCandidate? previous = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) previous = Read(reader);
        if (previous is not null && previous.Status != DeploymentStatus.DeployFailed)
        {
            var priorTarget = JsonSerializer.Deserialize<DeploymentTargetSnapshot>(previous.TargetJson ?? "null", EnvironmentJson);
            if (previous.SourceCandidateId == sourceId && previous.ReceiptJson == source.ReceiptJson && priorTarget is not null
                && priorTarget.KubernetesNamespace == target.KubernetesNamespace && priorTarget.KubernetesDatasetId == target.KubernetesDatasetId)
            {
                await tx.CommitAsync(ct);
                return previous;
            }
            if (previous.Status is DeploymentStatus.Approved or DeploymentStatus.Deploying)
                throw new InvalidOperationException("Another release is already deploying to this environment.");
        }
        var id = $"deploy-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow.ToString(DateFormat);
        cmd.CommandText = $"INSERT INTO {T("deployment")}(id,project_id,commit_sha,commit_summary,status,requested_at,requested_by,approved_at,approved_by,receipt_json,environment_name,target_json,source_candidate_id) VALUES(@id,@project,@sha,@summary,'Approved',@now,@actor,@now,@actor,@receipt,@target,@snapshot,@source)";
        cmd.AddParam("@id", id); cmd.AddParam("@sha", source.CommitSha); cmd.AddParam("@summary", (object?)source.CommitSummary ?? DBNull.Value);
        cmd.AddParam("@now", now); cmd.AddParam("@actor", actor); cmd.AddParam("@receipt", source.ReceiptJson!);
        cmd.AddParam("@snapshot", JsonSerializer.Serialize(target, EnvironmentJson));
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }
}
