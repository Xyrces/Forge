namespace Forge.Core;

public interface IAgent
{
    string Id { get; }
    string Name { get; }
    AgentType Type { get; }
    AgentStatus Status { get; }
    Task ExecuteAsync(CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Pipeline container types — epics and stories feed the
    /// spec → groom chain; they are NOT units of engineering work
    /// and must never be claimed by the engineering dispatch loop.
    /// Everything else (task, dev, ecs, ui, bug, ...) is
    /// dispatchable, preserving operator-enqueued type names.
    /// </summary>
    public static bool IsContainer(string type) =>
        string.Equals(type, "epic", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "story", StringComparison.OrdinalIgnoreCase);
}

public record Result(bool Success, string Message, IEnumerable<string>? Artifacts = null);
