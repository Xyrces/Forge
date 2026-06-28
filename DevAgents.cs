using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents;

public abstract class DevAgentBase : IAgent
{
    protected readonly Kernel _kernel;
    protected readonly AgentConfig _config;
    protected readonly StateStore _stateStore;
    protected readonly string _workspaceRoot;

    public string Id => _config.Name.ToLowerInvariant().Replace(" ", "-");
    public string Name => _config.Name;
    public AgentType Type => _config.Type;
    public AgentStatus Status { get; protected set; } = AgentStatus.Idle;

    protected DevAgentBase(Kernel kernel, AgentConfig config, StateStore stateStore, string workspaceRoot)
    {
        _kernel = kernel;
        _config = config;
        _stateStore = stateStore;
        _workspaceRoot = workspaceRoot;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Status = AgentStatus.Running;
        try
        {
            var state = await _stateStore.LoadStateAsync(cancellationToken);
            var myTasks = state.Tasks
                .Where(t => state.ActiveAgents.GetValueOrDefault(t.Id) == Name)
                .ToList();

            foreach (var task in myTasks.Where(t => t.Status == AgentTaskStatus.InProgress))
            {
                if (cancellationToken.IsCancellationRequested) break;
                await ProcessTaskAsync(task, cancellationToken);
            }
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
            var chatService = _kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();
            history.AddSystemMessage(BuildSystemPrompt());
            history.AddUserMessage(BuildContext(task));

            var response = await chatService.GetChatMessageContentsAsync(history, cancellationToken: cancellationToken);
            var resultMessage = response.FirstOrDefault()?.Content ?? "No response";

            if (resultMessage.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                task = task with { Status = AgentTaskStatus.Failed, Error = resultMessage };
                Status = AgentStatus.Error;
                return new Result(false, resultMessage);
            }

            task = task with { Status = AgentTaskStatus.Completed, CompletedAt = DateTime.UtcNow };
            var state = await _stateStore.LoadStateAsync(cancellationToken);
            var idx = state.Tasks.FindIndex(t => t.Id == task.Id);
            if (idx >= 0) state.Tasks[idx] = task;
            await _stateStore.SaveStateAsync(state, cancellationToken);

            Status = AgentStatus.Idle;
            return new Result(true, $"Task {task.Id} completed by {Name}");
        }
        catch (Exception ex)
        {
            task = task with { Status = AgentTaskStatus.Failed, Error = ex.Message };
            Status = AgentStatus.Error;
            return new Result(false, ex.Message);
        }
    }

    protected abstract string BuildSystemPrompt();
    protected abstract string BuildContext(AgentTask task);
}

public sealed class CoreDevAgent : DevAgentBase
{
    public CoreDevAgent(Kernel kernel, AgentConfig config, StateStore stateStore, string workspaceRoot)
        : base(kernel, config, stateStore, workspaceRoot) { }

    protected override string BuildSystemPrompt() => $"""
        You are {Name}, a C#/.NET 10 development agent specializing in PortHorizon.Core.
        Rules: NO game logic in Godot, ECS components must be unmanaged structs, zero allocation in hot paths.
        Working directory: {_workspaceRoot}
        """;

    protected override string BuildContext(AgentTask task) => $"""
        Task: {task.Description}
        Type: {task.Type}
        Branch: {task.Branch}
        Implement feature in PortHorizon.Core/ following ECS architecture.
        """;
}

public sealed class ClientDevAgent : DevAgentBase
{
    public ClientDevAgent(Kernel kernel, AgentConfig config, StateStore stateStore, string workspaceRoot)
        : base(kernel, config, stateStore, workspaceRoot) { }

    protected override string BuildSystemPrompt() => $"""
        You are {Name}, a Godot 4.x development agent for PortHorizon.Client.
        Rules: Godot is ONLY a renderer, NO game logic in Client, asset-by-key references.
        Working directory: {_workspaceRoot}
        """;

    protected override string BuildContext(AgentTask task) => $"""
        Task: {task.Description}
        Type: {task.Type}
        Branch: {task.Branch}
        Implement feature in PortHorizon.Client/ following View-only architecture.
        """;
}

public sealed class QAAgent : DevAgentBase
{
    public QAAgent(Kernel kernel, AgentConfig config, StateStore stateStore, string workspaceRoot)
        : base(kernel, config, stateStore, workspaceRoot) { }

    protected override string BuildSystemPrompt() => $"""
        You are {Name}, a QA agent for PortHorizon game testing.
        Use MCP playtest harness to verify game functionality.
        Working directory: {_workspaceRoot}
        """;

    protected override string BuildContext(AgentTask task) => $"""
        Task: {task.Description}
        Type: {task.Type}
        Branch: {task.Branch}
        Execute playtest verification using MCP harness.
        """;
}