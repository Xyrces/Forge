using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Configuration;
using Forge.Core.Execution;

namespace Forge.Orchestrator.Workers;

internal static class WorkerToolchainManifest
{
    internal static object Apply(object manifest, WorkerAssignment assignment, WorkerOptions options)
    {
        var invocation = JsonSerializer.Deserialize<WorkerInvocation>(assignment.AssignmentJson);
        if (invocation?.Context is null || !invocation.Context.TryGetValue("trustedToolchainImage", out var image)) return manifest;
        KubernetesAgentRunner.ValidateImageDigest(image);
        const string stateRoot = "/var/lib/forge/";
        var worktree = assignment.WorktreePath;
        if (!worktree.StartsWith(stateRoot, StringComparison.Ordinal) || worktree.Split('/').Skip(1).Any(p => p is ".." or "." or ""))
            throw new InvalidOperationException("Toolchain checkout must be a normalized path inside the Forge state volume.");
        var checkoutRoot = stateRoot + "projects/" + assignment.ProjectId + "/.forge/worktrees/";
        if (assignment.ProjectId.IndexOfAny(['/', '\\']) >= 0 || !worktree.StartsWith(checkoutRoot, StringComparison.Ordinal)
            || worktree[checkoutRoot.Length..].Contains('/'))
            throw new InvalidOperationException("Toolchain mounts require one task checkout inside the assigned project's worktree directory.");
        var subPath = worktree[stateRoot.Length..];
        if (string.IsNullOrWhiteSpace(subPath)) throw new InvalidOperationException("Toolchain requires an assigned checkout, not the state root.");
        var document = JsonSerializer.SerializeToNode(manifest)!;
        var spec = document["spec"]!["template"]!["spec"]!;
        var agent = spec["containers"]![0]!;
        ((JsonArray)agent["env"]!).Add(JsonSerializer.SerializeToNode(new { name = "FORGE_TOOLCHAIN_SOCKET", value = "/run/forge-toolchain/command.sock" }));
        ((JsonArray)agent["volumeMounts"]!).Add(JsonSerializer.SerializeToNode(new { name = "toolchain-ipc", mountPath = "/run/forge-toolchain" }));
        var volumes = (JsonArray)spec["volumes"]!;
        foreach (var name in new[] { "toolchain-ipc", "toolchain-helper", "toolchain-tmp", "toolchain-git", "toolchain-init-tmp" })
            volumes.Add(JsonSerializer.SerializeToNode(new { name, emptyDir = new { sizeLimit = name is "toolchain-tmp" or "toolchain-git" or "toolchain-init-tmp" ? "2Gi" : "16Mi" } }));
        var security = new { runAsNonRoot = true, runAsUser = 10001, runAsGroup = 10001,
            allowPrivilegeEscalation = false, readOnlyRootFilesystem = true, capabilities = new { drop = new[] { "ALL" } },
            seccompProfile = new { type = "RuntimeDefault" } };
        spec["securityContext"] = JsonSerializer.SerializeToNode(new { fsGroup = 10001 });
        spec["initContainers"] = JsonSerializer.SerializeToNode(new object[] {
            new { name = "toolchain-helper", image = options.Image,
                command = new[] { "/bin/cp", "/app/forge-command-server", "/helper/forge-command-server" },
                securityContext = security, volumeMounts = new[] { new { name = "toolchain-helper", mountPath = "/helper" } } },
            new { name = "toolchain-git", image = options.Image,
                args = new[] { "--prepare-toolchain-git", worktree, "/snapshot" },
                securityContext = security,
                volumeMounts = new object[] {
                    new { name = "state", mountPath = "/var/lib/forge", readOnly = true },
                    new { name = "toolchain-git", mountPath = "/snapshot" },
                    new { name = "toolchain-init-tmp", mountPath = "/tmp" } } },
            new { name = "toolchain", image, restartPolicy = "Always",
                command = new[] { "/helper/forge-command-server", "--socket", "/run/forge-toolchain/command.sock", "--root", worktree },
                startupProbe = new { exec = new { command = new[] { "/helper/forge-command-server", "--probe", "/run/forge-toolchain/command.sock" } }, periodSeconds = 1, failureThreshold = 30 },
                securityContext = security,
                env = new[] { new { name = "HOME", value = "/tmp/home" }, new { name = "TMPDIR", value = "/tmp" },
                    new { name = "DOTNET_CLI_HOME", value = "/tmp/home" } },
                resources = new { requests = new { cpu = options.CpuRequest, memory = options.MemoryRequest },
                    limits = new { cpu = options.CpuLimit, memory = options.MemoryLimit } },
                volumeMounts = new object[] {
                    new { name = "state", mountPath = worktree, subPath, readOnly = assignment.Role == "Reviewer" },
                    new { name = "toolchain-ipc", mountPath = "/run/forge-toolchain" },
                    new { name = "toolchain-helper", mountPath = "/helper", readOnly = true },
                    new { name = "toolchain-tmp", mountPath = "/tmp" },
                    new { name = "toolchain-git", mountPath = "/run/forge-toolchain-git" },
                    new { name = "toolchain-git", mountPath = worktree + "/.git",
                        subPath = Directory.Exists(Path.Combine(worktree, ".git")) ? "repo" : "marker" } } }
        });
        return document;
    }
}
