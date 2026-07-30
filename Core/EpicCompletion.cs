namespace Forge.Core;

/// <summary>
/// Epic completion rule: an epic CLOSES automatically when its
/// entire tree is terminal — every spec under it is past grooming
/// (or terminal), and every story and task in those specs is
/// Completed/Closed. Any Failed/Blocked descendant keeps the epic
/// open (operator decision, per the no-auto-clear rule), as does
/// any live work or a spec that hasn't been produced yet. The rule
/// is idempotent and read-only; the caller performs the transition.
/// </summary>
public static class EpicCompletion
{
    public sealed record Decision(bool ShouldClose, string Reason);

    private static readonly SpecStatus[] TerminalSpecStates =
        { SpecStatus.Groomed, SpecStatus.Shipped, SpecStatus.Superseded, SpecStatus.Archived };

    public static Decision Evaluate(
        IssueRecord epic,
        IReadOnlyList<SpecRecord> specsForEpic,
        IReadOnlyList<IssueRecord> allIssues)
    {
        if (specsForEpic.Count == 0)
        {
            return new(false, "no spec yet (intake/design pending)");
        }
        foreach (var spec in specsForEpic)
        {
            if (!TerminalSpecStates.Contains(spec.Status))
            {
                return new(false, $"spec {spec.Id} is {spec.Status} (not past grooming)");
            }
        }

        var stories = allIssues.Where(i =>
            i.Type == "story" && specsForEpic.Any(s => s.Id == i.ParentIssueId)).ToList();
        var tasks = allIssues.Where(i =>
            i.Type == "task" && stories.Any(s => s.Id == i.ParentIssueId)).ToList();

        foreach (var item in stories.Concat(tasks))
        {
            if (item.Status is IssueStatus.Failed or IssueStatus.Blocked)
            {
                return new(false, $"{item.Id} is {item.Status} — operator decision required");
            }
            if (item.Status is not (IssueStatus.Completed or IssueStatus.Closed))
            {
                return new(false, $"{item.Id} is {item.Status} (work still open)");
            }
        }

        var taskIds = tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var liveWatch = allIssues.Any(i =>
            i.Type == AgentTaskTypes.PrWatch
            && i.Status is IssueStatus.Pending or IssueStatus.InProgress
            && taskIds.Contains(i.GetMetadata("taskId") ?? ""));
        if (liveWatch)
        {
            return new(false, "a PR watch is still live");
        }

        return new(true, "all descendants terminal");
    }
}
