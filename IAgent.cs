namespace PortHorizon.Agents.Core;

public interface IAgent
{
    string Id { get; }
    string Name { get; }
    AgentType Type { get; }
    AgentStatus Status { get; }
    Task ExecuteAsync(CancellationToken cancellationToken = default);
    Task<Result> ProcessTaskAsync(AgentTask task, CancellationToken cancellationToken = default);
}

public enum AgentType
{
    Orchestrator,
    CoreDev,
    ClientDev,
    QA,
    Reviewer,
    Intake,
}

public enum AgentStatus
{
    Idle,
    Running,
    Waiting,
    Error,
    Stopped
}

public static class AgentTaskTypes
{
    public const string PrWatch = "pr-watch";
}

public record Result(bool Success, string Message, IEnumerable<string>? Artifacts = null);

public record AgentTask(
    string Id,
    string Type,
    string Description,
    Dictionary<string, object> Parameters,
    string Branch,
    AgentTaskStatus Status = AgentTaskStatus.Pending,
    string? Error = null,
    DateTime CreatedAt = default,
    DateTime? UpdatedAt = null,
    DateTime? CompletedAt = null
);

public enum AgentTaskStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Blocked
}
