using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Configuration;
using Forge.Core.Db;
using Forge.Projects;

namespace Forge.Orchestrator.Control;

/// <summary>Uses the existing privileged release supervisor; controller never receives initializer SQL credentials.</summary>
public sealed class KubernetesProjectProvisioner(Func<HttpClient> kubeFactory, AgentOptions options) : IProjectProvisioner
{
    internal Func<string, IDbConnectionFactory>? RuntimeFactory { get; init; }
    public async Task EnsureReadyAsync(string projectId, CancellationToken ct)
    {
        var schema = ProjectStorageInitializer.Schema(projectId);
        var kube = kubeFactory();
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.Worker.Image, @"^[a-zA-Z0-9][a-zA-Z0-9._:/-]*@sha256:[0-9a-f]{64}$"))
            throw new InvalidOperationException("Project provisioning requires this controller's immutable image.");
        var identity = options.Controller.DatasetId + "/" + projectId + "/" + options.Worker.Image;
        var prefix = "forge-project-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        var token = deadline.Token;
        var path = "apis/batch/v1/namespaces/" + options.Worker.Namespace + "/jobs/";
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var name = prefix + "-" + attempt;
            var desired = JsonSerializer.SerializeToElement(Manifest(options, projectId, name));
            using var get = await kube.GetAsync(path + name, token);
            var launched = get.StatusCode == HttpStatusCode.NotFound;
            if (launched)
            {
                using var created = await kube.PostAsJsonAsync(path.TrimEnd('/'), desired, token);
                if (!created.IsSuccessStatusCode && created.StatusCode != HttpStatusCode.Conflict)
                    throw new InvalidOperationException("Project provisioning supervisor could not be created. Check controller Job permissions.");
            }
            else get.EnsureSuccessStatusCode();
            while (true)
            {
                using var observed = await kube.GetAsync(path + name, token);
                observed.EnsureSuccessStatusCode();
                var job = await observed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                if ((job.GetProperty("metadata").TryGetProperty("ownerReferences", out var owners) && owners.GetArrayLength() > 0) || !Contains(job, desired)) throw new InvalidOperationException("Provisioning supervisor differs from the immutable request.");
                if (job.TryGetProperty("status", out var status) && status.TryGetProperty("conditions", out var conditions))
                {
                    if (conditions.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "Failed" && c.GetProperty("status").GetString() == "True"))
                    {
                        if (launched) throw new InvalidOperationException("Project initializer failed. Inspect retained Job diagnostics before retrying registration.");
                        using var childResponse = await kube.GetAsync(path + name + "-init", token);
                        if (childResponse.StatusCode != HttpStatusCode.NotFound)
                        {
                            childResponse.EnsureSuccessStatusCode();
                            var child = await childResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                            if (!child.TryGetProperty("status", out var childStatus) || !childStatus.TryGetProperty("conditions", out var childConditions)
                                || !childConditions.EnumerateArray().Any(c => c.GetProperty("type").GetString() is "Complete" or "Failed" && c.GetProperty("status").GetString() == "True"))
                                throw new InvalidOperationException("Previous project initializer is still running; retry after it has stopped.");
                        }
                        using var podsResponse = await kube.GetAsync("api/v1/namespaces/" + options.Worker.Namespace + "/pods?labelSelector=job-name%3D" + name + "-init", token);
                        podsResponse.EnsureSuccessStatusCode();
                        var pods = await podsResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                        if (pods.GetProperty("items").EnumerateArray().Any(p => !p.TryGetProperty("status", out var ps) || !ps.TryGetProperty("phase", out var phase) || phase.GetString() is not ("Succeeded" or "Failed") || (ps.TryGetProperty("containerStatuses", out var containers) && containers.EnumerateArray().Any(c => c.TryGetProperty("state", out var state) && state.TryGetProperty("running", out _)))))
                            throw new InvalidOperationException("Previous project initializer pod has not stopped.");
                        break;
                    }
                    if (conditions.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "Complete" && c.GetProperty("status").GetString() == "True"))
                    {
                        var runtime = RuntimeFactory?.Invoke(schema)
                            ?? ForgeDb.SqlServer(options.Db.ConnectionString, schema, initializeSchema: false);
                        await ControllerHost.VerifySchemaAsync(runtime, token);
                        await MemoryAuthoritySql.VerifyAsync(runtime, options.Bootstrap.WorkerPrincipal, token);
                        return;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            // Terminal failed jobs retain diagnostics. A subsequent deterministic name
            // allows retry without deleting an initializer that may still be executing.
        }
        throw new InvalidOperationException("Repeated provisioning failures require operator inspection.");
    }

    internal static bool Contains(JsonElement actual, JsonElement expected) => expected.ValueKind switch
    {
        JsonValueKind.Object => actual.ValueKind == JsonValueKind.Object && expected.EnumerateObject().All(p => actual.TryGetProperty(p.Name, out var v) && Contains(v, p.Value)),
        JsonValueKind.Array => actual.ValueKind == JsonValueKind.Array && actual.GetArrayLength() == expected.GetArrayLength()
            && expected.EnumerateArray().Select((v, i) => Contains(actual[i], v)).All(x => x),
        _ => actual.ToString() == expected.ToString()
    };

    internal static object Manifest(AgentOptions options, string projectId, string name) => new
    {
        apiVersion = "batch/v1", kind = "Job", metadata = new { name, @namespace = options.Worker.Namespace },
        spec = new { backoffLimit = 0, activeDeadlineSeconds = 900, template = new { spec = new {
            restartPolicy = "Never", serviceAccountName = "forge-release", automountServiceAccountToken = true,
            securityContext = new { runAsUser = 10001, runAsGroup = 10001, fsGroup = 10001, runAsNonRoot = true, seccompProfile = new { type = "RuntimeDefault" } },
            containers = new[] { new { name = "provision", image = options.Worker.Image,
                command = new[] { "python3", "/app/deploy/k3s/project_provision.py" },
                args = new[] { "--namespace", options.Worker.Namespace, "--image", options.Worker.Image,
                    "--dataset", options.Controller.DatasetId, "--project", projectId, "--operation", name },
                securityContext = new { allowPrivilegeEscalation = false, capabilities = new { drop = new[] { "ALL" } } },
                resources = new { requests = new { cpu = "100m", memory = "128Mi" }, limits = new { cpu = "1", memory = "1Gi" } }
            } }
        } } }
    };
}
