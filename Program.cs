using Microsoft.SemanticKernel;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var taskId = args.FirstOrDefault(a => a.StartsWith("--task"))?.Split('=').LastOrDefault();
        var branch = args.FirstOrDefault(a => a.StartsWith("--branch"))?.Split('=').LastOrDefault() ?? "main";

        if (string.IsNullOrEmpty(taskId))
        {
            Console.WriteLine("Usage: PortHorizon.Agents --task=<id> --branch=<name>");
            return;
        }

        var stateStore = new StateStore();
        var workspaceRoot = @"C:\Users\jtn50\repos\gamedev\PortHorizon";
        var gitHubService = new GitHubService("Xyrces", "PortHorizon");

        var kernel = Kernel.CreateBuilder().Build();

        var config = new AgentConfig(
            AgentType.CoreDev, "DevAgent", "", "", new List<string>());

        var agent = new CoreDevAgent(kernel, config, stateStore, workspaceRoot);

        var state = await stateStore.LoadStateAsync();
        var task = state.Tasks.FirstOrDefault(t => t.Id == taskId);

        if (task == null)
        {
            Console.WriteLine($"Task {taskId} not found");
            return;
        }

        var result = await agent.ProcessTaskAsync(task);
        Console.WriteLine(result.Success ? $"SUCCESS: {result.Message}" : $"FAILED: {result.Message}");
    }
}