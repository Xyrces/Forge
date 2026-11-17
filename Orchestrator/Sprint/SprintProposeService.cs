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
    private readonly SprintActivationValidator _activation;
    internal StageGates? Gates { get; }


    public SprintProposeService(
        IIssueStore issues,
        ISprintStore sprints,
        IScorer scorer,
        SprintProposalAuditStore audit,
        StageGates? gates = null,
        ISpecStore? specs = null,
        string? projectId = null)
    {
        _issues = issues;
        _sprints = sprints;
        _scorer = scorer;
        _audit = audit;
        Gates = gates;
        var concrete = issues as IssueStore;
        _activation = new SprintActivationValidator(issues, sprints,
            specs ?? (concrete is not null ? new SpecStore(concrete) : throw new ArgumentException("A specification store is required.")),
            projectId ?? concrete?.ProjectId ?? throw new ArgumentException("A project id is required."), gates);
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
            .Select(s => new ProposedCandidate(s.Task.Id, s.Task.Title, s.Score, s.Breakdown))
            .ToArray();

        var fingerprints = await _activation.PreviewFingerprintsAsync(
            candidates.Select(c => c.TaskId), candidateCount, ct);
        candidates = candidates.Where(c => fingerprints.ContainsKey(c.TaskId)).ToArray();
        weights["activationSnapshots"] = fingerprints;
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

        if (audit.CommittedSprintId is not null)
            throw new InvalidOperationException("This proposal has already been committed.");
        using var weights = System.Text.Json.JsonDocument.Parse(audit.WeightsJson);
        if (!weights.RootElement.TryGetProperty("activationSnapshots", out var fingerprints))
            throw new InvalidOperationException("This proposal is stale; generate a new proposal.");
        if (taskIds.Count == 0 || taskIds.Distinct(StringComparer.Ordinal).Count() != taskIds.Count)
            throw new InvalidOperationException("Select one or more distinct eligible tasks.");
        var validated = new List<SprintActivationSnapshot>();
        foreach (var id in taskIds)
        {
            if (!fingerprints.TryGetProperty(id, out var fingerprint))
                throw new InvalidOperationException($"Task {id} was not an eligible candidate in this proposal.");
            var current = await _activation.ValidateAsync([id], ct);
            validated.Add(current);
            if (fingerprint.GetString() != SprintActivationValidator.Fingerprint(current))
                throw new InvalidOperationException($"Proposal is stale: task {id} or its specification changed.");
        }
        var issueGroups = validated.SelectMany(s => s.Issues).GroupBy(i => i.Id).ToArray();
        var specGroups = validated.SelectMany(s => s.Specs).GroupBy(s => s.Id).ToArray();
        if (issueGroups.Any(g => g.Distinct().Count() != 1) || specGroups.Any(g => g.Distinct().Count() != 1))
            throw new InvalidOperationException("Proposal changed during validation; generate a new proposal.");
        var snapshot = new SprintActivationSnapshot(issueGroups.Select(g => g.First()).ToArray(),
            specGroups.Select(g => g.First()).ToArray(), taskIds, auditId, committedBy);
        if (Gates is not null && await Gates.IsHeldAsync(StageGates.Sprint, ct))
            throw new InvalidOperationException("Sprint assembly is held by the operator.");
        var start = DateTime.UtcNow;
        var sprint = await _sprints.CreateWithIssuesAsync(new NewSprint(
            Name: theme ?? "Proposed Sprint",
            Goal: goal ?? $"Auto-proposed from audit {auditId}",
            StartDate: start,
            EndDate: start.AddDays(14),
            Status: SprintStatus.Active), snapshot, ct);

        return sprint.Id;
    }
}