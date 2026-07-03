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
    // P2.a: Designer agent lifecycle events. Namespaced under
    // 'designer.' so the dashboard's SSE filter can scope to them.
    public const string DesignerRunStarted = "designer.run.started";
    public const string DesignerRunCompleted = "designer.run.completed";
    public const string DesignerRunFailed = "designer.run.failed";
    public const string DesignerArtifactSaved = "designer.artifact.saved";
    public const string DesignerStatusCommitted = "designer.status.committed";
    // P2.b: Artist agent lifecycle events. Namespaced under
    // 'artist.' so the dashboard's SSE filter can scope to them.
    public const string ArtistRunStarted = "artist.run.started";
    public const string ArtistRunCompleted = "artist.run.completed";
    public const string ArtistRunFailed = "artist.run.failed";
    public const string ArtistArtSaved = "artist.art.saved";
    public const string ArtistStatusCommitted = "artist.status.committed";
}