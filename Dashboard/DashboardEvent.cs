namespace Forge.Dashboard;

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
    // Reviewer-agent lifecycle: published when a review run starts
    // (event-driven PR-open trigger AND the sweep's review path) so
    // the Events stream shows activity between PR-open and verdict.
    public const string ReviewStarted = "pr.review.started";
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
    // P4 Stage A: recovery events emitted by StartupRecovery.
    public const string RecoveryAction = "dispatch.recovery";
    // P5.5: memory extraction events. Emitted by
    // CommitPushPrExecutor after the PR is opened; the data dict
    // carries extractedCount + persistedKeys.
    public const string MemoryExtracted = "memory.extracted";
    // P6 Stage 6: intake agent lifecycle events. Promoted from raw
    // string literals in IntakeAgent.cs so the SSE subscription and
    // the dashboard's Live Feed don't drift apart.
    public const string IntakeRunStarted = "intake.run.started";
    public const string IntakeRunFailed = "intake.run.failed";
    public const string IntakeRunCompleted = "intake.run.completed";
    // Sprint flow: emitted by SprintAssembler when a sprint's member
    // tasks all reach terminal states (sprint.completed) and when the
    // next sprint is assembled + activated (sprint.started).
    public const string SprintCompleted = "sprint.completed";
    public const string SprintStarted = "sprint.started";
    // Inter-sprint build visibility (operator request 2026-08-06):
    // the phase BETWEEN sprint.completed and sprint.started (batch
    // triage → materialization → follow-up grooming) was only
    // visible in journal logs — a completed sprint looked "stuck".
    // SprintAssembler publishes these and snapshots the full state
    // to the project's memory store (sprint/build) every tick;
    // GET /api/sprints/building reads it back.
    public const string SprintTriageCompleted = "sprint.triage.completed";
    public const string SprintMaterialized = "sprint.materialized";
    public const string SprintAssemblyWaiting = "sprint.assembly.waiting";
    // ScheduledGroomer ad-hoc pass: one event per groomed task so the
    // board shows the grooming countdown draining in real time.
    public const string GroomerAdHocCompleted = "groomer.adhoc.completed";
    // Editable workflow: definition published / restored from the
    // Flow page Edit mode. Detail carries the human-readable diff.
    public const string WorkflowPublished = "workflow.published";
    // Watchdog: a new structural-stall finding (alert-only v1).
    // Detail carries "watchdog/<kind>: <detail>"; Data carries
    // projectId + severity.
    public const string WatchdogFinding = "watchdog.finding";
}