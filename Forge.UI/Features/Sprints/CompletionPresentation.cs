namespace Forge.Dashboard.Features.Sprints;

public static class CompletionPresentation
{
    public static string Outcome(string? value) => value switch
    {
        "Satisfied" => "Required evidence satisfied",
        "PendingEvidence" => "Awaiting required evidence",
        "Incomplete" => "Incomplete — operator attention needed",
        "LegacyTerminalOnly" => "Legacy terminal counts only — delivery unproven",
        "Abandoned" => "Stopped by operator — not delivered",
        _ => "Unknown evidence state"
    };
    public static string Processing(string? value) => value switch
    {
        "Pending" => "Queued",
        "Running" => "Processing",
        "Decided" => "Applying recorded result",
        "NeedsReview" => "Review needed before retry",
        "Completed" => "Finished",
        "Abandoned" or "Stopped" => "Stopped by operator",
        _ => "Unknown processing state"
    };
    public static string Evidence(string? value) => value switch
    {
        "MergedAndVerified" => "Merged work with recorded verification",
        "ImplementationVerified" => "Implementation independently verified and reviewed",
        "DeploymentHealthy" => "Healthy deployment",
        "OperatorObserved" => "Explicit operator observation",
        _ => "Unknown evidence requirement"
    };
    public static string Deployment(string? value) => value switch
    {
        null => "Not recorded",
        "Pending" => "Awaiting qualification",
        "BuildPassed" => "Qualified; awaiting release",
        "BuildFailed" => "Qualification failed — review required",
        "Approved" => "Release authorized; not yet started",
        "Deploying" => "Release in progress",
        "Deployed" => "Release healthy",
        "DeployFailed" => "Release failed — review required",
        "Rejected" => "Release rejected",
        _ => "Unknown release state"
    };
}
