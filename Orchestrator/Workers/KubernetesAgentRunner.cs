using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Execution;

namespace Forge.Orchestrator.Workers;

public sealed class WorkerExecutionUncertainException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record WorkerInvocation(AgentType Role, string Prompt, string? SessionId, Dictionary<string, string> Context);

public interface IWorkerJobs
{
    Task EnsureJobAsync(WorkerAssignment assignment, CancellationToken ct);
    Task<string?> GetFailureAsync(string attemptId, CancellationToken ct);
    Task CancelAndWaitAsync(string attemptId, CancellationToken ct);
}

/// <summary>Wait cancellation detaches the controller; only a persisted cancellation request stops a worker.</summary>
public sealed class KubernetesAgentRunner(WorkerAttemptStore store, IWorkerJobs jobs, string datasetId,
    Func<CancellationToken, Task>? ensureAdmission = null) : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId,
        IReadOnlyDictionary<string, object>? context, CancellationToken ct)
    {
        var values = context?.ToDictionary(x => x.Key, x => x.Value?.ToString() ?? "") ?? [];
        string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Worker invocation requires {key}.");
        var project = Required("projectId");
        var task = Required("issueId");
        var worktree = Required("worktreePath");
        if (!Path.IsPathFullyQualified(worktree)) throw new InvalidOperationException("Worker worktree path must be absolute on the shared volume.");
        var dispatch = values.GetValueOrDefault("dispatchId");
        if (string.IsNullOrWhiteSpace(dispatch))
        {
            if (role is AgentType.CoreDev or AgentType.ClientDev)
                throw new InvalidOperationException("Engineering worker invocation requires durable dispatchId.");
            dispatch = Hash(prompt);
        }
        var key = Hash(string.Join("\n", datasetId, project, task, role, dispatch));
        var id = key[..40];
        if (ensureAdmission is not null) await ensureAdmission(ct);
        var assignment = await store.GetAsync(id, ct);
        if (assignment is null)
        {
            var sourceSha = values.GetValueOrDefault("sourceSha");
            if (string.IsNullOrWhiteSpace(sourceSha)) sourceSha = await ReadHeadAsync(worktree, ct);
            values["sourceSha"] = sourceSha;
            var invocation = new WorkerInvocation(role, prompt, sessionId, values);
            assignment = await store.CreateOrGetAsync(new(id, datasetId, key, project, task, role.ToString(), dispatch,
                sourceSha, worktree, JsonSerializer.Serialize(invocation), Guid.NewGuid().ToString("N")), ct);
        }
        if (assignment.State == "queued")
        {
            if (ensureAdmission is not null) await ensureAdmission(ct);
            try { await jobs.EnsureJobAsync(assignment, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new WorkerExecutionUncertainException("Worker Job creation outcome is uncertain; preserve checkout and reconcile before retry.", ex); }
        }
        while (true)
        {
            try { assignment = await store.GetAsync(id, ct) ?? throw new InvalidOperationException("Worker assignment disappeared."); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new WorkerExecutionUncertainException("Worker state could not be read; preserve checkout before retry.", ex); }
            if (assignment.State is "succeeded" or "failed" or "cancelled")
            {
                try
                {
                    using var termination = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    termination.CancelAfter(TimeSpan.FromSeconds(60));
                    await jobs.CancelAndWaitAsync(id, termination.Token);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                { throw new WorkerExecutionUncertainException("Worker recorded a result but pod termination is unconfirmed; preserve checkout.", ex); }
            }
            if (assignment.State == "succeeded")
                return JsonSerializer.Deserialize<AgentRunResult>(assignment.ResultJson!)
                    ?? throw new InvalidOperationException("Worker result was empty.");
            if (assignment.State is "failed" or "cancelled")
                throw new InvalidOperationException($"Worker {id} {assignment.State}: {assignment.Error}");
            try
            {
                if (await jobs.GetFailureAsync(id, ct) is { } failure)
                {
                    await store.MarkJobFailedAsync(id, failure, ct);
                    throw new WorkerExecutionUncertainException(failure + " Reconcile pod termination before retry.");
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or WorkerExecutionUncertainException))
            { throw new WorkerExecutionUncertainException("Worker Job observation failed; preserve checkout before retry.", ex); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public async Task CancelDispatchAsync(AgentType role, string projectId, string taskId, string dispatchId, CancellationToken ct)
    {
        var id = Hash(string.Join("\n", datasetId, projectId, taskId, role, dispatchId))[..40];
        var assignment = await store.GetAsync(id, ct);
        if (assignment is null) return;
        await store.RequestCancellationAsync(id, ct);
        await jobs.CancelAndWaitAsync(id, ct);
        await store.MarkJobFailedAsync(id, "Run deadline: worker Job and pods were deleted before retry.", ct);
    }

    internal static async Task<string> ReadHeadAsync(string worktree, CancellationToken ct)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = worktree, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add("HEAD");
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not read worker source SHA.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        var sha = (await output).Trim();
        if (process.ExitCode != 0 || sha.Length is not (40 or 64) || sha.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Cannot resolve worker source SHA: " + await error);
        return sha;
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

public sealed class KubernetesWorkerJobs(HttpClient http, WorkerOptions options) : IWorkerJobs
{
    public async Task EnsureJobAsync(WorkerAssignment assignment, CancellationToken ct)
    {
        var manifest = BuildManifest(assignment, options);
        using var response = await http.PostAsJsonAsync($"apis/batch/v1/namespaces/{Uri.EscapeDataString(options.Namespace)}/jobs", manifest, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return; // deterministic name: replacement controller adopts
        response.EnsureSuccessStatusCode();
    }

    public async Task<string?> GetFailureAsync(string attemptId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"apis/batch/v1/namespaces/{Uri.EscapeDataString(options.Namespace)}/jobs/forge-agent-{attemptId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return "Worker Job disappeared before its SQL result was recorded.";
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.TryGetProperty("status", out var status)
            && status.TryGetProperty("conditions", out var conditions))
            foreach (var condition in conditions.EnumerateArray())
                if (condition.GetProperty("status").GetString() == "True"
                    && condition.GetProperty("type").GetString() is "Failed" or "Complete")
                    return "Worker Job terminated without a SQL result: " + condition.GetProperty("type").GetString();
        return null;
    }

    public async Task CancelAndWaitAsync(string attemptId, CancellationToken ct)
    {
        var path = $"apis/batch/v1/namespaces/{Uri.EscapeDataString(options.Namespace)}/jobs/forge-agent-{attemptId}";
        using var delete = new HttpRequestMessage(HttpMethod.Delete, path)
        {
            Content = JsonContent.Create(new { apiVersion = "v1", kind = "DeleteOptions", propagationPolicy = "Foreground" })
        };
        using var response = await http.SendAsync(delete, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
        while (true)
        {
            using var observed = await http.GetAsync(path, ct);
            if (observed.StatusCode == HttpStatusCode.NotFound) return;
            observed.EnsureSuccessStatusCode();
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    public static object BuildManifest(WorkerAssignment assignment, WorkerOptions options)
    {
        if (options.Image.LastIndexOf("@sha256:", StringComparison.Ordinal) is var digestIndex
            && (digestIndex < 1 || options.Image.Length - digestIndex != 72
                || options.Image[(digestIndex + 8)..].Any(c => !Uri.IsHexDigit(c))))
            throw new InvalidOperationException("Worker image must be pinned by immutable sha256 digest.");
        return new
        {
            apiVersion = "batch/v1", kind = "Job",
            metadata = new { name = "forge-agent-" + assignment.Id, @namespace = options.Namespace,
                labels = new Dictionary<string, string> { ["app.kubernetes.io/name"] = "forge-worker", ["forge-attempt"] = assignment.Id } },
            spec = new
            {
                backoffLimit = 0, activeDeadlineSeconds = options.DeadlineSeconds, ttlSecondsAfterFinished = 86400,
                template = new
                {
                    metadata = new { labels = new Dictionary<string, string> { ["app.kubernetes.io/name"] = "forge-worker", ["forge-attempt"] = assignment.Id } },
                    spec = new
                    {
                        restartPolicy = "Never", serviceAccountName = "forge-worker", automountServiceAccountToken = false,
                        containers = new[] { new {
                            name = "agent", image = options.Image,
                            args = new[] { "--config", "/run/secrets/config/appsettings.json" },
                            env = new object[] {
                                new { name = "Controller__Mode", value = "worker" },
                                new { name = "Db__Provider", value = "sqlserver" },
                                new { name = "GITHUB_PACKAGES_USER", valueFrom = new { secretKeyRef = new { name = "forge-worker-packages", key = "username" } } },
                                new { name = "GITHUB_PACKAGES_PAT", valueFrom = new { secretKeyRef = new { name = "forge-worker-packages", key = "token" } } },
                                new { name = "Db__AutoMigrate", value = "false" },
                                new { name = "Db__ConnectionString", valueFrom = new { secretKeyRef = new { name = "forge-worker-runtime", key = "sql-connection-string" } } },
                                new { name = "Worker__AttemptId", value = assignment.Id },
                                new { name = "Worker__PodUid", valueFrom = new { fieldRef = new { fieldPath = "metadata.uid" } } } },
                            resources = new {
                                requests = new { cpu = options.CpuRequest, memory = options.MemoryRequest },
                                limits = new { cpu = options.CpuLimit, memory = options.MemoryLimit } },
                            volumeMounts = new object[] {
                                new { name = "state", mountPath = "/var/lib/forge" },
                                new { name = "config", mountPath = "/run/secrets/config", readOnly = true } }
                        } },
                        volumes = new object[] {
                            new { name = "state", persistentVolumeClaim = new { claimName = "forge-state" } },
                            new { name = "config", secret = new { secretName = "forge-config" } } }
                    }
                }
            }
        };
    }

    public static HttpClient CreateInClusterClient()
    {
        const string root = "/var/run/secrets/kubernetes.io/serviceaccount/";
        var handler = CreateClusterTlsHandler(root + "ca.crt");
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") ?? "kubernetes.default.svc";
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT_HTTPS") ?? "443";
        var client = new HttpClient(new RotatingTokenHandler(root + "token", handler)) { BaseAddress = new Uri($"https://{host}:{port}/") };
        return client;
    }

    internal static HttpClientHandler CreateClusterTlsHandler(string certificatePath)
        => new ClusterTlsHandler(certificatePath);

    private sealed class ClusterTlsHandler : HttpClientHandler
    {
        private readonly X509Certificate2 _certificate;

        public ClusterTlsHandler(string certificatePath)
        {
            // Kubernetes mounts a public CA certificate, never its private key.
            _certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
            ServerCertificateCustomValidationCallback = (_, server, _, errors) =>
            {
                if (server is null || (errors & (System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch
                    | System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(_certificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(server);
            };
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _certificate.Dispose();
        }
    }

    private sealed class RotatingTokenHandler(string path, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(path, ct)).Trim());
            return await base.SendAsync(request, ct);
        }
    }

}
