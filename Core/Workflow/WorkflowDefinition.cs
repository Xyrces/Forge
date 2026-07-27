namespace Forge.Core.Workflow;

/// <summary>
/// The operator-facing workflow definition: a JSON-serializable
/// description of the pipeline DAG (steps, edges, gate placement,
/// policy values) that the Flow page renders from and later passes
/// edit (draft + publish). Edges are DESCRIPTIVE — they drive
/// rendering and gate placement, never execution. The transition
/// table (<see cref="TaskStateMachine"/>) and event reporters stay
/// code-owned; an edit can only re-wire or re-parameterize behavior
/// the machinery already knows how to honor.
/// </summary>
public sealed record WorkflowStep(
    string Id,
    string Label,
    string Lane,
    string Kind,
    bool Optional,
    bool Enabled,
    int X,
    int Y,
    IReadOnlyList<string> Gates);

public sealed record WorkflowEdge(
    string From,
    string To,
    string Kind,
    string? Label,
    string? Condition);

public sealed record WorkflowDefinition(
    int Version,
    IReadOnlyList<WorkflowStep> Steps,
    IReadOnlyList<WorkflowEdge> Edges,
    IReadOnlyDictionary<string, string> Policies);

public static class WorkflowLanes
{
    public const string Planning = "planning";
    public const string Implementation = "implementation";
}

public static class WorkflowEdgeKinds
{
    public const string Happy = "happy";
    public const string Branch = "branch";
    public const string Loop = "loop";
    public const string Failure = "failure";
}

public static class WorkflowPolicies
{
    public const string MaxStrikes = "maxStrikes";
    public const string StallGraceMinutes = "stallGraceMinutes";
    public const string ParkOnInfra = "parkOnInfra";
    public const string AutoMerge = "autoMerge";
    public const string NoDiffOutcome = "noDiffOutcome";
}
