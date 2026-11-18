namespace Forge.Dashboard.Services;

/// <summary>Presentation of recorded facts. Never infer task delivery from a model run.</summary>
public static class WorkPresentation
{
    public sealed record PlanSection(string Heading, string Body);
    public static IReadOnlyList<PlanSection> PlanSections(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return [];
        var sections = new List<PlanSection>();
        var heading = "Recorded plan";
        var body = new System.Text.StringBuilder();
        var fenced = false;
        foreach (var line in plan.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal)) fenced = !fenced;
            var match = fenced ? null : System.Text.RegularExpressions.Regex.Match(line, @"^#{1,3}\s+(.+?)\s*$");
            if (match?.Success == true)
            {
                if (body.Length > 0 && !string.IsNullOrWhiteSpace(body.ToString())) sections.Add(new(heading, body.ToString().Trim()));
                heading = match.Groups[1].Value; body.Clear();
            }
            else body.AppendLine(line);
        }
        if (!string.IsNullOrWhiteSpace(body.ToString())) sections.Add(new(heading, body.ToString().Trim()));
        return sections;
    }

    public static string Role(string? role) => role?.ToLowerInvariant() switch
    {
        "clientdev" or "client" => "UI developer",
        "coredev" or "core" => "Core developer",
        "reviewer" => "Forge reviewer",
        "qa" => "QA verifier",
        "designer" => "Designer",
        "groomer" => "Technical planner",
        "intake" => "Intake",
        _ => role ?? "Actor not recorded"
    };

    public static string RunOutcome(string status, string? role) => status switch
    {
        "succeeded" when string.Equals(role, "Reviewer", StringComparison.OrdinalIgnoreCase) => "Review completed",
        "succeeded" => "Agent work completed",
        "running" => "In progress",
        "failed" => "Run failed",
        "cancelled" or "canceled" => "Cancelled",
        _ => status
    };

    public static string GateName(string gate) => gate switch
    {
        "plan-schema" => "Plan structure",
        "plan-territory" => "Files in scope",
        "plan-llm-review" => "Automated plan critique",
        _ => gate
    };

    public static bool GateWarning(string feedback) =>
        feedback.Contains("warning", StringComparison.OrdinalIgnoreCase)
        || feedback.Contains("unparseable", StringComparison.OrdinalIgnoreCase)
        || feedback.Contains("fails open", StringComparison.OrdinalIgnoreCase);

    public static string EventName(string kind) => kind switch
    {
        "created" => "Task created",
        "claimed" => "Worker claimed task",
        "transition" => "Task state changed",
        "checkpoint" => "Work checkpoint recorded",
        "metadata" => "Evidence updated",
        "recovery" => "Recovery recorded",
        _ => System.Text.RegularExpressions.Regex.Replace(kind.Replace('-', ' ').Replace('_', ' '), "([a-z])([A-Z])", "$1 $2")
    };
}
