using Forge.Agents;
using Forge.Configuration;
using Forge.Core;

namespace Forge.Orchestrator;

/// <summary>The same approved outcome planner used by automatic assembly. Commit never changes its goal or member set.</summary>
public sealed class SprintProposeService
{
    public const int DefaultCandidateCount = 7;
    private readonly ISprintStore _sprints;
    private readonly SprintProposalAuditStore _audit;
    private readonly SprintActivationValidator _activation;
    private readonly SprintPlanBuilder _planner;
    private readonly IssueStore _issues;
    private readonly ProjectOptions? _projectOptions;
    internal StageGates? Gates { get; }
    internal SprintPlanningOptions Planning { get; }

    public SprintProposeService(IIssueStore issues, ISprintStore sprints, IScorer scorer, SprintProposalAuditStore audit,
        StageGates? gates = null, ISpecStore? specs = null, string? projectId = null,
        SprintPlanningOptions? planning = null, IReadOnlyDictionary<string, int>? roleCaps = null, ProjectOptions? projectOptions = null)
    {
        var concrete = issues as IssueStore ?? throw new ArgumentException("Approved sprint planning requires the owning project store.");
        _issues = concrete; _projectOptions = projectOptions;
        specs ??= new SpecStore(concrete); projectId ??= concrete.ProjectId;
        _sprints = sprints; _audit = audit; Gates = gates; Planning = planning ?? new();
        _activation = new(issues, sprints, specs, projectId, gates, planning, roleCaps);
        _planner = new(concrete, sprints, specs, projectId, gates, planning, roleCaps);
    }

    public sealed record ProposedCandidate(string TaskId, string Title, int Score, IReadOnlyList<string> Breakdown);
    public sealed record ProposalResult(string? Theme, string? Goal, DateTime ScoredAt,
        IReadOnlyList<ProposedCandidate> Candidates, IReadOnlyList<string> SelectedTaskIds,
        IReadOnlyDictionary<string, object> Weights, long AuditId);

    public async Task<ProposalResult> ProposeAsync(string? theme, string? goal, int candidateCount = DefaultCandidateCount, CancellationToken ct = default)
    {
        var result = await _planner.BuildAsync(ct);
        var snapshot = result.Snapshot;
        var plan = snapshot?.Plan;
        var selected = snapshot?.TaskIds ?? [];
        var candidates = snapshot?.Issues.Where(i => selected.Contains(i.Id))
            .Select(i => new ProposedCandidate(i.Id, i.Title, 0, ["Explicit approved criterion mapping; complete prerequisite closure."])).ToArray() ?? [];
        var weights = new Dictionary<string, object> { ["reason"] = result.Reason, ["deferred"] = result.Deferred };
        if (plan is not null)
        {
            weights["plan"] = System.Text.Json.JsonSerializer.SerializeToElement(plan, SprintPlanJson.Options);
            weights["activationFingerprint"] = SprintActivationValidator.Fingerprint(snapshot!);
        }
        var audit = await _audit.RecordAsync(plan?.Objective, plan?.Objective, weights,
            candidates.Select(c => new SprintProposeCandidate(c.TaskId, c.Title, c.Score, c.Breakdown)).ToArray(), selected, ct);
        return new(plan?.Objective, plan?.Objective, DateTime.UtcNow, candidates, selected, weights, audit);
    }

    public async Task<string> CommitAsync(long auditId, IReadOnlyList<string> taskIds, string? theme, string? goal,
        string committedBy, CancellationToken ct = default)
    {
        var audit = await _audit.GetAsync(auditId, ct) ?? throw new InvalidOperationException("Proposal not found.");
        if (audit.CommittedSprintId is not null) throw new InvalidOperationException("Proposal already committed.");
        var plan = SprintPlanJson.ReadAuditPlan(audit.WeightsJson) ?? throw new InvalidOperationException("Proposal has no complete approved outcome; generate a new proposal.");
        if (!taskIds.Order(StringComparer.Ordinal).SequenceEqual(plan.Members.Select(m => m.TaskId).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Cannot substitute or partially select an outcome's members.");
        if ((!string.IsNullOrWhiteSpace(goal) && goal != plan.Objective) || (!string.IsNullOrWhiteSpace(theme) && theme != plan.Objective))
            throw new InvalidOperationException("An approved outcome cannot be replaced by a manual goal.");
        // Revalidate complete selected units; prerequisite mappings may have another unit identity,
        // so only direct delivery roots seed semantics while dependency traversal adds prerequisites.
        var roots = plan.Members.Where(m => m.Reason != SprintMemberReason.Prerequisite).Select(m => m.TaskId).ToArray();
        var snapshot = await _activation.ValidateAsync(roots, ct);
        using var weights = System.Text.Json.JsonDocument.Parse(audit.WeightsJson);
        if (!weights.RootElement.TryGetProperty("activationFingerprint", out var old)
            || old.GetString() != SprintActivationValidator.Fingerprint(snapshot))
            throw new InvalidOperationException("Proposal is stale; generate a new proposal.");
        snapshot = snapshot with { Plan = plan, ProposalAuditId = auditId, CommittedBy = committedBy };
        var start = DateTime.UtcNow;
        Forge.Core.Completion.CompletionPolicySnapshot? policy = null;
        if (_projectOptions is not null)
        {
            var project = await new Forge.Deploy.DeploymentStore(_issues.Db).GetEffectiveProjectAsync(_projectOptions, ct);
            if (project.Deployment is { } deployment && deployment.Environments.Count > 0)
            {
                var target = deployment.SprintEnvironment is { } name ? DeploymentEnvironmentResolver.Capture(project, name) : null;
                policy = new(target?.Name, target is not null && deployment.Environments[target.Name].RequireApproval, target is null ? null : System.Text.Json.JsonSerializer.Serialize(target));
            }
        }
        var sprint = await _sprints.CreateWithIssuesAsync(new(plan.Objective, plan.Objective, start, start.AddDays(14), CompletionPolicy: policy), snapshot, ct);
        return sprint.Id;
    }
}
