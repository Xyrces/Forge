using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Configuration;

namespace Forge.Deploy;

// The release Job outlives this controller. Its ConfigMap journal is reconciled after replacement.
public sealed class KubernetesDeploymentExecutor(HttpClient http) : IIndependentDeploymentExecutor
{
    // Target dashboards receive no Kubernetes credentials. Redirects must not change the authority being probed.
    private static readonly HttpClient DashboardProbe = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    }) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<string> ReadTargetRevisionAsync(DeploymentTargetSnapshot target, CancellationToken ct)
    {
        if (!Regex.IsMatch(target.KubernetesNamespace, "^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$"))
            throw new InvalidOperationException("Invalid target namespace.");
        var url = $"http://forge.{target.KubernetesNamespace}.svc:8080/api/meta/buildinfo";
        using var build = await DashboardProbe.GetFromJsonAsync<JsonDocument>(url, ct);
        var revision = build?.RootElement.GetProperty("sourceRevision").GetString();
        return revision is not null && Regex.IsMatch(revision, "^[0-9a-f]{40}$") ? revision
            : throw new InvalidOperationException($"Could not verify the serving source in {target.DisplayName}.");
    }

    public async Task<ProjectOptions> ResolveProfileAsync(ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct)
    {
        project = ApplyTarget(project, candidate);
        if (candidate.CompletionId is not null || candidate.EnvironmentName is not null || candidate.SourceCandidateId is not null)
        {
            var pinnedReceipt = JsonSerializer.Deserialize<TrustedImageReceipt>(candidate.ReceiptJson ?? "null", new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Routine release receipt is missing.");
            var options = project.Deployment ?? throw new InvalidOperationException("Deployment configuration missing.");
            if (pinnedReceipt.SourceSha != candidate.CommitSha || !pinnedReceipt.BuildPassed || !pinnedReceipt.TestPassed ||
                !string.Equals(pinnedReceipt.Repository, TrustedGitHubReleaseSource.Repository(project), StringComparison.OrdinalIgnoreCase) ||
                pinnedReceipt.Branch != project.DefaultBranch || pinnedReceipt.Workflow != options.RoutineWorkflow ||
                pinnedReceipt.ValidationWorkflow != options.RoutineValidationWorkflow ||
                !long.TryParse(pinnedReceipt.WorkflowRunId, out var run) || run <= 0 ||
                !long.TryParse(pinnedReceipt.ValidationRunId, out var validationRun) || validationRun <= 0 ||
                !long.TryParse(pinnedReceipt.ArtifactId, out var artifact) || artifact <= 0 ||
                pinnedReceipt.SchemaVersion != Forge.Core.IssueStore.CurrentSchemaVersion ||
                !Regex.IsMatch(pinnedReceipt.ArtifactDigest, "^sha256:[0-9a-f]{64}$") ||
                !Regex.IsMatch(pinnedReceipt.ImageDigest, "^" + Regex.Escape(options.RoutineImageRepository) + "@sha256:[0-9a-f]{64}$"))
                throw new InvalidOperationException("Pinned routine receipt does not match configured release authority.");
            var pinnedProject = project with { Deployment = options with { KubernetesImages = new() { [candidate.CommitSha] = pinnedReceipt.ImageDigest } } };
            ResolveImage(pinnedProject, candidate);
            return pinnedProject;
        }
        var catalog = project.Deployment?.KubernetesImageCatalog;
        if (string.IsNullOrEmpty(catalog)) throw new InvalidOperationException("Kubernetes deployment requires a reviewed CI image catalog.");
        if (!Regex.IsMatch(catalog, "^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$") ||
            !Regex.IsMatch(project.Deployment!.KubernetesNamespace, "^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$") ||
            !Regex.IsMatch(candidate.CommitSha, "^[0-9a-f]{40}$"))
            throw new InvalidOperationException("Invalid immutable catalog lookup.");
        using var document = await http.GetFromJsonAsync<JsonDocument>($"api/v1/namespaces/{project.Deployment.KubernetesNamespace}/configmaps/{catalog}", ct)
            ?? throw new InvalidOperationException("Reviewed image catalog is unavailable.");
        if (!document.RootElement.GetProperty("data").TryGetProperty(candidate.CommitSha, out var entry))
            throw new InvalidOperationException("No reviewed image receipt has been registered for this source SHA.");
        using var receipt = JsonDocument.Parse(entry.GetString() ?? "{}");
        var record = receipt.RootElement;
        if (record.GetProperty("sourceSha").GetString() != candidate.CommitSha ||
            !record.GetProperty("buildPassed").GetBoolean() || !record.GetProperty("testPassed").GetBoolean() ||
            record.GetProperty("workflow").GetString() != "controller-image.yml" ||
            string.IsNullOrWhiteSpace(record.GetProperty("workflowRunId").GetString()))
            throw new InvalidOperationException("Image receipt does not attest successful CI build and tests for this source.");
        var resolved = project with { Deployment = project.Deployment with {
            KubernetesImages = new() { [candidate.CommitSha] = record.GetProperty("imageDigest").GetString() ?? "" } } };
        ResolveImage(resolved, candidate);
        return resolved;
    }

    public static string ResolveImage(ProjectOptions project, DeploymentCandidate candidate)
    {
        var options = project.Deployment ?? throw new InvalidOperationException("Deployment configuration is required.");
        if (!options.RequireBuildCheck || !Regex.IsMatch(candidate.CommitSha, "^[0-9a-fA-F]{40}$") ||
            !options.KubernetesImages.TryGetValue(candidate.CommitSha, out var image) ||
            !Regex.IsMatch(image, "^[a-zA-Z0-9][a-zA-Z0-9._:/-]*@sha256:[0-9a-f]{64}$"))
            throw new InvalidOperationException("Kubernetes deployment requires a build gate and a reviewed full source SHA to immutable image digest mapping.");
        if (!Guid.TryParse(options.KubernetesDatasetId, out _) ||
            !Regex.IsMatch(options.KubernetesNamespace, "^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$") ||
            !Regex.IsMatch(candidate.Id, candidate is { Status: DeploymentStatus.Deployed, EnvironmentName: not null, CompletionId: null, SourceCandidateId: null }
                ? "^[a-z0-9][-a-z0-9]{0,47}[a-z0-9]$" : "^deploy-[0-9a-f]{32}$"))
            throw new InvalidOperationException("Invalid Kubernetes dataset, namespace, or deployment operation ID.");
        return image;
    }

    public static ProjectOptions ApplyTarget(ProjectOptions project, DeploymentCandidate candidate)
    {
        if (candidate.EnvironmentName is null)
        {
            if (candidate.TargetJson is not null)
                throw new InvalidOperationException("Deployment target has no environment identity.");
            return project; // Pre-environment candidates retain the legacy target.
        }
        DeploymentTargetSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<DeploymentTargetSnapshot>(candidate.TargetJson ?? "null", new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Deployment target snapshot is missing.");
        }
        catch (JsonException ex) { throw new InvalidOperationException("Deployment target snapshot is invalid.", ex); }
        if (snapshot.Name != candidate.EnvironmentName)
            throw new InvalidOperationException("Deployment environment does not match its captured target.");
        return DeploymentEnvironmentResolver.Apply(project, snapshot);
    }

    public async Task<DeploymentExecutionResult> ExecuteAsync(ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct = default)
    {
        if (candidate.Status != DeploymentStatus.BuildPassed)
            return new(false, "Kubernetes release requires a successful build and test gate.");
        string image;
        try { project = await ResolveProfileAsync(project, candidate, ct); image = ResolveImage(project, candidate); }
        catch (InvalidOperationException ex) { return new(false, ex.Message); }
        var ns = project.Deployment!.KubernetesNamespace;
        var path = $"apis/batch/v1/namespaces/{ns}/jobs";
        using var response = await http.PostAsJsonAsync(path, BuildManifest(project, candidate), ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            using var existing = await http.GetFromJsonAsync<JsonDocument>(path + "/forge-release-" + candidate.Id, ct)
                ?? throw new InvalidOperationException("Existing release Job could not be read.");
            var annotations = existing.RootElement.GetProperty("metadata").GetProperty("annotations");
            if (annotations.GetProperty("forge/source-sha").GetString() != candidate.CommitSha ||
                annotations.GetProperty("forge/image").GetString() != image ||
                annotations.GetProperty("forge/dataset").GetString() != project.Deployment.KubernetesDatasetId)
                return new(false, "Existing release Job has different immutable inputs; refusing adoption.");
        }
        else response.EnsureSuccessStatusCode();
        return new(false, "Independent release Job accepted; durable progress is available in the deployment list.", true);
    }

    public async Task<DeploymentExecutionResult> ReadProgressAsync(ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct, Func<CancellationToken, Task<bool>>? canStart = null)
    {
        project = await ResolveProfileAsync(project, candidate, ct);
        var image = ResolveImage(project, candidate);
        using var response = await http.GetAsync($"api/v1/namespaces/{project.Deployment!.KubernetesNamespace}/configmaps/forge-release-{candidate.Id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            using var jobResponse = await http.GetAsync($"apis/batch/v1/namespaces/{project.Deployment.KubernetesNamespace}/jobs/forge-release-{candidate.Id}", ct);
            if (jobResponse.StatusCode == HttpStatusCode.NotFound)
            {
                if (canStart is null || !await canStart(ct))
                    return new(false, "Release has not started; awaiting deployment admission.", true, AdmissionHeld: true);
                // The SQL approval may have committed just before its controller stopped. Deterministic Job name makes retry safe.
                return await ExecuteAsync(project, candidate with { Status = DeploymentStatus.BuildPassed }, ct);
            }
            jobResponse.EnsureSuccessStatusCode();
            using var job = await JsonDocument.ParseAsync(await jobResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (job.RootElement.TryGetProperty("status", out var status) && status.TryGetProperty("conditions", out var conditions) &&
                conditions.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "Failed" && c.GetProperty("status").GetString() == "True"))
                return new(false, "Release Job failed before creating a journal; inspect its logs. No cutover was recorded.");
            return new(false, "Waiting for release journal.", true);
        }
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var data = json.RootElement.GetProperty("data");
        if (data.GetProperty("image").GetString() != image || data.GetProperty("dataset").GetString() != project.Deployment.KubernetesDatasetId)
            throw new InvalidOperationException("Release journal identity does not match approved inputs.");
        var phase = data.GetProperty("phase").GetString();
        if (phase == "committed" && candidate.EnvironmentName is not null &&
            (!data.TryGetProperty("targetVerified", out var verified) || verified.GetString() != "true" ||
             !data.TryGetProperty("environment", out var environment) || environment.GetString() != candidate.EnvironmentName ||
             !data.TryGetProperty("sourceSha", out var source) || source.GetString() != candidate.CommitSha ||
             !data.TryGetProperty("namespace", out var ns) || ns.GetString() != project.Deployment.KubernetesNamespace))
            return new(false, "Release journal lacks the approved environment's routed dashboard and revision verification.");
        return phase switch
        {
            "committed" => new(true, "Release committed; new controller owns admission and the dashboard route."),
            "rolled-back" => new(false, "Release failed; rollback completed and the previous controller was restored."),
            _ => new(false, $"Independent release phase: {phase}. Inspect release Job if progress stops; do not delete its journal.", true)
        };
    }

    public static object BuildManifest(ProjectOptions project, DeploymentCandidate candidate)
    {
        project = ApplyTarget(project, candidate);
        var image = ResolveImage(project, candidate);
        var options = project.Deployment!;
        return new {
            apiVersion = "batch/v1", kind = "Job",
            metadata = new { name = "forge-release-" + candidate.Id, @namespace = options.KubernetesNamespace,
                annotations = new Dictionary<string, string> { ["forge/source-sha"] = candidate.CommitSha,
                    ["forge/image"] = image, ["forge/dataset"] = options.KubernetesDatasetId! } },
            spec = new { backoffLimit = 3, activeDeadlineSeconds = 3600,
                template = new { spec = new {
                    restartPolicy = "Never", serviceAccountName = "forge-release", automountServiceAccountToken = true,
                    securityContext = new { runAsUser = 10001, runAsGroup = 10001, fsGroup = 10001,
                        runAsNonRoot = true, seccompProfile = new { type = "RuntimeDefault" } },
                    containers = new[] { new { name = "release", image,
                        command = new[] { "python3", "/app/deploy/k3s/release.py" },
                        args = new[] { "--namespace", options.KubernetesNamespace, "--image", image,
                            "--dataset", options.KubernetesDatasetId!, "--operation", candidate.Id, "--source-sha", candidate.CommitSha }
                            .Concat(candidate.EnvironmentName is null ? [] : new[] { "--environment", candidate.EnvironmentName }).ToArray(),
                        env = new[] {
                            new { name = "POD_NAME", valueFrom = new { fieldRef = new { fieldPath = "metadata.name" } } },
                            new { name = "POD_UID", valueFrom = new { fieldRef = new { fieldPath = "metadata.uid" } } } },
                        securityContext = new { allowPrivilegeEscalation = false, capabilities = new { drop = new[] { "ALL" } } },
                        resources = new { requests = new { cpu = "100m", memory = "128Mi" }, limits = new { cpu = "1", memory = "1Gi" } },
                        volumeMounts = new[] { new { name = "control-token", mountPath = "/run/forge-control", readOnly = true } } } },
                    volumes = new[] { new { name = "control-token", secret = new { secretName = "forge-control", defaultMode = 288 } } }
                } }
            }
        };
    }
}
