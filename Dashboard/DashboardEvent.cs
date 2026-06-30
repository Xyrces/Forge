namespace PortHorizon.Agents.Dashboard;

public sealed record DashboardEvent(
    DateTime Timestamp,
    string Kind,
    string? TaskId,
    string? Detail,
    IReadOnlyDictionary<string, object?>? Data = null);

public static class DashboardEventKind
{
    public const string TaskTransition = "task.transition";
    public const string AgentSessionStarted = "acp.session.started";
    public const string AgentSessionCompleted = "acp.session.completed";
    public const string AgentSessionFailed = "acp.session.failed";
    public const string PrOpened = "pr.opened";
    public const string PrMerged = "pr.merged";
    public const string PrChangesRequested = "pr.changes-requested";
    public const string PrFailed = "pr.failed";
    public const string Log = "log";
}