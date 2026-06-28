using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents;

public sealed class OrchestratorAgent : IAgent
{
    private readonly Kernel _kernel;
    private readonly AgentRegistry _registry;
    private readonly StateStore _stateStore;
    private readonly AgentProcessSpawner _spawner;
    private readonly GitHubService _gitHubService;

    public string Id => "orchestrator";
    public string Name => "OrchestratorAgent";
    public AgentType Type => AgentType.Orchestrator;
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;

    public OrchestratorAgent(
        Kernel kernel,
        AgentRegistry registry,
        StateStore stateStore,
        AgentProcessSpawner spawner,
        GitHubService gitHubService)
    {
        _kernel = kernel;
        _registry = registry;
        _stateStore = stateStore;
        _spawner = spawner;
        _gitHubService = gitHubService;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Status = AgentStatus.Running;

        try
        {
            var state = await _stateStore.LoadStateAsync(cancellationToken);
            var pendingTasks = state.Tasks.Where(t => t.Status == AgentTaskStatus.Pending).ToList();

            foreach (var task in pendingTasks)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await DispatchTaskAsync(task, cancellationToken);
            }

            await _stateStore.SaveStateAsync(state, cancellationToken);
            Status = AgentStatus.Idle;
        }
        catch
        {
            Status = AgentStatus.Error;
            throw;
        }
    }

    public async Task<Result> ProcessTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        Status = AgentStatus.Running;

        try
        {
            var agentType = DetermineAgentType(task.Type);
            var agent = await _registry.GetLeastLoadedAsync(agentType, cancellationToken);

            if (agent == null)
                return new Result(false, $"No available agent for type {task.Type}");

            await _gitHubService.CreateBranchAsync(task.Branch, cancellationToken);

            var process = await _spawner.SpawnAgentAsync(
                agent.Name,
                agent.ProjectPath,
                $"--task {task.Id} --branch {task.Branch}",
                cancellationToken);

            var state = await _stateStore.LoadStateAsync(cancellationToken);
            state.Tasks.Add(task);
            state.ActiveAgents[task.Id] = agent.Name;
            await _stateStore.SaveStateAsync(state, cancellationToken);

            Status = AgentStatus.Idle;
            return new Result(true, $"Task {task.Id} dispatched to {agent.Name}");
        }
        catch (Exception ex)
        {
            Status = AgentStatus.Error;
            return new Result(false, ex.Message);
        }
    }

    private AgentType DetermineAgentType(string taskType) => taskType.ToLowerInvariant() switch
    {
        "ecs" or "systems" or "pathfinding" or "atmospherics" or "mcp" => AgentType.CoreDev,
        "client" or "ui" or "godot" or "syncbridge" => AgentType.ClientDev,
        "test" or "playtest" or "qa" => AgentType.QA,
        "review" => AgentType.Reviewer,
        _ => AgentType.CoreDev
    };

    private async Task DispatchTaskAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var agentType = DetermineAgentType(task.Type);
        var agent = await _registry.GetLeastLoadedAsync(agentType, cancellationToken);

        if (agent == null)
        {
            await Task.Delay(5000, cancellationToken);
            return;
        }

        var process = await _spawner.SpawnAgentAsync(
            agent.Name,
            agent.ProjectPath,
            $"--task {task.Id} --branch {task.Branch}",
            cancellationToken);

        var state = await _stateStore.LoadStateAsync(cancellationToken);
        var taskIdx = state.Tasks.FindIndex(t => t.Id == task.Id);
        if (taskIdx >= 0)
            state.Tasks[taskIdx] = task with { Status = AgentTaskStatus.InProgress };
        state.ActiveAgents[task.Id] = agent.Name;
        await _stateStore.SaveStateAsync(state, cancellationToken);
    }
}