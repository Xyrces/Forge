using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// One task + the deterministic-scoring breakdown. Used by
/// the ScrumMasterAgent as a signal in its prompt; the agent's
/// free-form <c>Rationale</c> is the primary explainability
/// artifact. The score breakdown lands in
/// <c>sprint_selection.score_breakdown</c> alongside the rationale.
///
/// <para>
/// The score formula is the one from §7.2 of the workflow doc:
/// <code>
///   + 10 if priority == 1 (highest)
///   + 6  if priority == 2
///   + 3  if priority == 3
///   + 2  if priority == 4
///   + 1  if priority == 5
///   + 5  if parent story's spec matches the sprint theme
///   + 2  per day of age (capped at +10)
///   - 20 if a downstream task is already in another sprint
/// </code>
/// </para>
///
/// <para>
/// NOT an LLM agent. Pure C# rules class. Lives here so the
/// ScrumMasterAgent can call it as a tool and have a structured
/// signal in its prompt, alongside its free-form reasoning.
/// </para>
/// </summary>
public interface IScorer
{
    DeterministicScorer.ScoredBacklog Score(
        IReadOnlyList<IssueRecord> pendingTasks,
        string? theme,
        IReadOnlySet<string> taskIdsInOtherSprints);
}

public sealed class DeterministicScorer : IScorer
{
    public sealed record ScoredTask(IssueRecord Task, int Score, IReadOnlyList<string> Breakdown);
    public sealed record ScoredBacklog(IReadOnlyList<ScoredTask> Items, DateTime ScoredAt);

    public ScoredBacklog Score(
        IReadOnlyList<IssueRecord> pendingTasks,
        string? theme,
        IReadOnlySet<string> taskIdsInOtherSprints)
    {
        var now = DateTime.UtcNow;
        var items = new List<ScoredTask>(pendingTasks.Count);
        foreach (var t in pendingTasks)
        {
            var breakdown = new List<string>();
            int score = 0;

            // Priority.
            score += t.Priority switch
            {
                1 => 10,
                2 => 6,
                3 => 3,
                4 => 2,
                5 => 1,
                _ => 0,
            };
            breakdown.Add($"+{score - (score - PriorityScore(t.Priority))} priority={t.Priority}");

            // Theme match: cheap heuristic — title contains the theme
            // (case-insensitive substring). The agent can overrule.
            if (!string.IsNullOrWhiteSpace(theme)
                && t.Title.Contains(theme, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
                breakdown.Add("+5 theme-match");
            }

            // Age: +2 per day since CreatedAt, capped at +10.
            var ageDays = Math.Max(0, (now - t.CreatedAt).TotalDays);
            var ageBonus = (int)Math.Min(10, ageDays * 2);
            if (ageBonus > 0)
            {
                score += ageBonus;
                breakdown.Add($"+{ageBonus} age");
            }

            // Downstream penalty: if the task is in another sprint,
            // big penalty. The SprintStore doesn't have a "what's in
            // other sprints" query today; we accept an ISet.
            if (taskIdsInOtherSprints.Contains(t.Id))
            {
                score -= 20;
                breakdown.Add("-20 in-other-sprint");
            }

            items.Add(new ScoredTask(t, score, breakdown));
        }
        // Sort descending by score.
        items.Sort((a, b) => b.Score.CompareTo(a.Score));
        return new ScoredBacklog(items, now);
    }

    private static int PriorityScore(int p) => p switch
    {
        1 => 10,
        2 => 6,
        3 => 3,
        4 => 2,
        5 => 1,
        _ => 0,
    };
}