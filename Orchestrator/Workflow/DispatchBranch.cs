using System.Security.Cryptography;
using System.Text;
using Forge.AgentTools;

namespace Forge.Orchestrator.Workflow;

/// <summary>Fresh databases may reuse issue IDs; their Git branches must not reuse old work.</summary>
public static class DispatchBranch
{
    public static string Resolve(string? existingBranch, string? datasetId, string projectId, string taskId)
    {
        // Recovery and rework retain the branch already associated with their PR.
        if (!string.IsNullOrWhiteSpace(existingBranch)) return existingBranch;
        var task = GitRefNames.Sanitize(taskId);
        if (string.IsNullOrWhiteSpace(datasetId)) return $"agent/{task}";
        var identity = System.Text.Json.JsonSerializer.Serialize(new[] { datasetId, projectId });
        var dataset = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $"agent/{dataset}/{task}";
    }
}
