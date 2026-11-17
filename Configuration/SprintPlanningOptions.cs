namespace Forge.Configuration;

public sealed record SprintPlanningOptions
{
    // A ceiling, never a target. Do not pad a useful outcome with optional work.
    public int MaxEngineeringTasks { get; set; } = 10;
    public int CandidatePageSize { get; set; } = 100;
}
