using System.ComponentModel;
using Forge.Core;
using Microsoft.Extensions.AI;

namespace Forge.AgentTools;

public sealed class WorkerDependencyTool(IssueStore issues, string taskId)
{
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        ([Description("Missing executable, library or runtime.")] string tool,
         [Description("Observed failure and why this task needs the dependency. Do not include secrets.")] string reason,
         [Description("Suggested repository Dockerfile change for operator review, not a command to execute now.")] string suggestedChange)
            => RequestAsync(tool, reason, suggestedChange),
        name: "request_worker_dependency",
        description: "Propose a missing worker dependency on the current task for operator review. Records a proposal only; does not install anything, approve an image, create work, or retry the task.");

    public async Task<string> RequestAsync(string tool, string reason, string suggestedChange)
    {
        try
        {
            var id = await issues.RequestWorkerDependencyAsync(taskId, tool, reason, suggestedChange);
            return $"Recorded dependency proposal {id}. Await operator review; the worker environment has not changed.";
        }
        catch (ArgumentException ex) { return "Invalid dependency proposal: " + ex.Message; }
    }
}
