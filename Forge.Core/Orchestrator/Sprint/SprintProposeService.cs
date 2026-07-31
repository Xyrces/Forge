using Forge.Agents;
using Forge.Core;

namespace Forge.Orchestrator;

/// <summary>
/// P6 Stage 8: deterministic sprint proposal.
///
/// <para>
/// Pulls pending tasks, runs the scorer, writes a v14 audit row,
/// and returns the top-N candidates with their breakdown strings
/// (the dashboard renders these verbatim in the SprintPropose view's
/// "Explainability Column").
/// </para>
/// </summary>
public sealed class SprintProposeService
{
    public const int DefaultCandidateCount = 7;

    private readonly IIssueStore _issues;
    private readonly ISprintStore _sprints;
    private readonly IScorer _scorer;
    private readonly SprintProposalAuditStore _audit;

    public SprintProposeService(
        IIssueStore issues,
        ISprintStore sprints,
        IScorer scorer,
        SprintProposalAuditStore audit)
    {
        _issues = issues;
        _sprints = sprints;
        _scorer = scorer;
        _audit = audit;
    }

    public sealed record ProposedCandidate(
        string TaskId,
        string Title,
        int Score,
        IReadOnlyList<string> Breakdown);

    public sealed record ProposalResult(
        string? Theme,
        string? Goal,
        DateTime ScoredAt,
        IReadOnlyList<ProposedCandidate> Candidates,
        IReadOnlyList<string> SelectedTaskIds,
        IReadOnlyDictionary<string, object> Weights,
        long AuditId);

    public async Task<ProposalResult> ProposeAsync(
        string? theme,
        string? goal,
        int candidateCount = DefaultCandidateCount,
        CancellationToken ct = default)
    {
        var allIssues = await _issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }, ct);
        var inOtherSprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeSprints = await _sprints.ListAsync(activeOnly: true, ct);
        foreach (var s in activeSprints)
        {
            foreach (var id in await _sprints.GetIssueIdsAsync(s.Id, ct))
            {
                inOtherSprints.Add(id);
            }
        }

        var scored = _scorer.Score(allIssues, theme, inOtherSprints);

        var weights = new Dictionary<string, object>
        {
            ["priority"] = new Dictionary<string, int> { ["1"] = 10, ["2"] = 6, ["3"] = 3, ["4"] = 2, ["5"] = 1 },
            ["themeMatch"] = 5,
            ["agePerDay"] = 2,
            ["ageCap"] = 10,
            ["inOtherSprint"] = -20,
        };

        var candidates = scored.Items
            .Take(candidateCount)
            .Select(s => new ProposedCandidate(s.Task.Id, s.Task.Title, s.Score, s.Breakdown))
            .ToArray();

        var selectedTaskIds = candidates.Select(c => c.TaskId).ToArray();

        var auditCandidates = candidates
            .Select(c => new SprintProposeCandidate(c.TaskId, c.Title, c.Score, c.Breakdown))
            .ToArray();

        var auditId = await _audit.RecordAsync(
            theme, goal, weights, auditCandidates, selectedTaskIds, ct);

        return new ProposalResult(
            Theme: theme,
            Goal: goal,
            ScoredAt: scored.ScoredAt,
            Candidates: candidates,
            SelectedTaskIds: selectedTaskIds,
            Weights: weights,
            AuditId: auditId);
    }

    public async Task<string> CommitAsync(
        long auditId,
        IReadOnlyList<string> taskIds,
        string? theme,
        string? goal,
        string committedBy,
        CancellationToken ct = default)
    {
        var audit = await _audit.GetAsync(auditId, ct)
            ?? throw new InvalidOperationException($"audit row {auditId} not found");

        var start = DateTime.UtcNow;
        var sprint = await _sprints.CreateAsync(new NewSprint(
            Name: theme ?? "Proposed Sprint",
            Goal: goal ?? $"Auto-proposed from audit {auditId}",
            StartDate: start,
            EndDate: start.AddDays(14),
            Status: SprintStatus.Active), ct);

        foreach (var tid in taskIds)
        {
            await _sprints.AddIssueAsync(sprint.Id, tid, ct);
        }

        await _audit.MarkCommittedAsync(auditId, sprint.Id, committedBy, ct);
        return sprint.Id;
    }
}