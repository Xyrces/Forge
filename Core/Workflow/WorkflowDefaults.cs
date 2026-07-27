namespace Forge.Core.Workflow;

/// <summary>
/// The built-in default workflow — byte-for-byte the pipeline that
/// used to be hardcoded in Dashboard/Flow/FlowGraph.cs, plus the
/// policy values that used to be constants. This is the definition
/// the resolver falls back to when no live override is published,
/// and the baseline "Reset to default" restores. Coordinates are
/// the Live-view SVG layout.
/// </summary>
public static class WorkflowDefaults
{
    public const int CurrentVersion = 1;

    public static readonly WorkflowDefinition Definition = new(
        Version: CurrentVersion,
        Steps: new WorkflowStep[]
        {
            // Planning lane (left → right).
            new("intake",  "Intake",          WorkflowLanes.Planning, "stage", Optional: false, Enabled: true, X: 60,  Y: 90, Gates: Array.Empty<string>()),
            new("design",  "Design",          WorkflowLanes.Planning, "stage", Optional: true,  Enabled: true, X: 210, Y: 90, Gates: new[] { StageGates.Design }),
            new("groom",   "Groom",           WorkflowLanes.Planning, "stage", Optional: false, Enabled: true, X: 360, Y: 90, Gates: new[] { StageGates.Groom }),
            new("backlog", "Groomed backlog", WorkflowLanes.Planning, "stage", Optional: false, Enabled: true, X: 510, Y: 90, Gates: Array.Empty<string>()),
            new("sprint",  "Sprint",          WorkflowLanes.Planning, "stage", Optional: false, Enabled: true, X: 660, Y: 90, Gates: new[] { StageGates.Sprint }),
            // Implementation lane (snakes right → left under planning).
            new("setup",   "Dispatching",            WorkflowLanes.Implementation, "stage", Optional: false, Enabled: true, X: 660, Y: 290, Gates: Array.Empty<string>()),
            new("agent",   "Agent run",              WorkflowLanes.Implementation, "stage", Optional: false, Enabled: true, X: 510, Y: 290, Gates: Array.Empty<string>()),
            new("pr",      "PR open (CI + review)",  WorkflowLanes.Implementation, "stage", Optional: false, Enabled: true, X: 360, Y: 290, Gates: Array.Empty<string>()),
            new("review",  "Approved / merge-ready", WorkflowLanes.Implementation, "stage", Optional: true,  Enabled: true, X: 210, Y: 290, Gates: new[] { StageGates.Merge }),
            new("done",    "Merged / done",          WorkflowLanes.Implementation, "stage", Optional: false, Enabled: true, X: 60,  Y: 290, Gates: Array.Empty<string>()),
            // Loop + failure sinks below the implementation lane.
            new("rework",  "Rework loop",            WorkflowLanes.Implementation, "loop", Optional: false, Enabled: true, X: 360, Y: 420, Gates: Array.Empty<string>()),
            new("parked",  "Parked (infra wait)",    WorkflowLanes.Implementation, "sink", Optional: true,  Enabled: true, X: 210, Y: 420, Gates: Array.Empty<string>()),
            new("blocked", "Blocked / failed",       WorkflowLanes.Implementation, "sink", Optional: false, Enabled: true, X: 60,  Y: 420, Gates: Array.Empty<string>()),
        },
        Edges: new WorkflowEdge[]
        {
            new("intake", "design",  WorkflowEdgeKinds.Branch, "visual",            "send-to-designer"),
            new("intake", "groom",   WorkflowEdgeKinds.Branch, "non-visual fast path", "operator approve"),
            new("design", "groom",   WorkflowEdgeKinds.Happy,  null,                "designed → groomable"),
            new("groom",  "backlog", WorkflowEdgeKinds.Happy,  null,                "spec groomed / ad-hoc approved"),
            new("backlog", "sprint", WorkflowEdgeKinds.Happy,  null,                "assembler ingests"),
            new("sprint", "setup",   WorkflowEdgeKinds.Happy,  null,                "dispatch claims"),
            new("setup",  "agent",   WorkflowEdgeKinds.Happy,  null,                "worktree ready → run"),
            new("agent",  "pr",      WorkflowEdgeKinds.Happy,  null,                "diff → commit/push/PR"),
            new("agent",  "done",    WorkflowEdgeKinds.Branch, "no changes",        "verified NO_CHANGES_NEEDED"),
            new("agent",  "blocked", WorkflowEdgeKinds.Failure, null,               "no-progress breaker / unrecoverable"),
            new("pr",     "review",  WorkflowEdgeKinds.Happy,  null,                "CI green + approval"),
            new("pr",     "rework",  WorkflowEdgeKinds.Branch, "on CI failure / changes requested / conflict", "rework round"),
            new("pr",     "parked",  WorkflowEdgeKinds.Branch, "on pre-existing base CI failure", "park until base recovers"),
            new("parked", "rework",  WorkflowEdgeKinds.Loop,   "base recovered",    "no-strike refresh round"),
            new("review", "done",    WorkflowEdgeKinds.Happy,  null,                "merge"),
            new("review", "rework",  WorkflowEdgeKinds.Branch, "on conflict after approval", "base moved — sync round"),
            new("rework", "agent",   WorkflowEdgeKinds.Loop,   null,                "redispatch, same branch/PR"),
            new("rework", "blocked", WorkflowEdgeKinds.Failure, null,               "rework circuit breaker"),
        },
        Policies: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkflowPolicies.MaxStrikes] = "3",
            [WorkflowPolicies.StallGraceMinutes] = "35",
            [WorkflowPolicies.ParkOnInfra] = "true",
            [WorkflowPolicies.AutoMerge] = "true",
            [WorkflowPolicies.NoDiffOutcome] = "completed",
        });
}
