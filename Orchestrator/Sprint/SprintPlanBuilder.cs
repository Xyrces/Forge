using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;

namespace Forge.Orchestrator;

internal sealed record SprintPlanBuildResult(SprintActivationSnapshot? Snapshot, IReadOnlyList<SprintPlanDeferral> Deferred, string Reason);

/// <summary>Deterministic outcome selection. Grooming supplies semantics once; no paid replanning loop.</summary>
internal sealed class SprintPlanBuilder(IssueStore issues, ISprintStore sprints, ISpecStore specs, string projectId,
    StageGates? gates, SprintPlanningOptions? options = null, IReadOnlyDictionary<string, int>? roleCaps = null)
{
    private string? _cacheKey;
    private string? _afterId;
    private SprintPlanBuildResult? _cache;
    private readonly SemaphoreSlim _planning = new(1, 1);

    public async Task<SprintPlanBuildResult> BuildAsync(CancellationToken ct = default)
    {
        await _planning.WaitAsync(ct);
        try { return await BuildCoreAsync(ct); }
        finally { _planning.Release(); }
    }

    private async Task<SprintPlanBuildResult> BuildCoreAsync(CancellationToken ct)
    {
        var policy = options ?? new SprintPlanningOptions();
        var validator = new SprintActivationValidator(issues, sprints, specs, projectId, gates, policy, roleCaps);
        var projection = new SprintPlanningStore(issues);
        var candidates = await projection.CandidatesAsync(Math.Clamp(policy.CandidatePageSize, 1, 100), ct: ct, afterId: _afterId);
        if (candidates.Count == 0 && _afterId is not null)
        { _afterId = null; candidates = await projection.CandidatesAsync(Math.Clamp(policy.CandidatePageSize, 1, 100), ct: ct); }
        _afterId = candidates.LastOrDefault()?.Id;
        // Explicit outcome identity retrieves related approved work even beyond this page.
        // A second bounded projection never implies the entire backlog was materialized.
        var firstMapped = candidates.Select(c => c.BindingJson).FirstOrDefault(j => j is { Length: <= 32768 });
        if (firstMapped is not null)
        {
            try
            {
                var seed = JsonSerializer.Deserialize<SprintOutcomeBinding>(firstMapped, SprintPlanJson.Options);
                if (seed?.OutcomeId is { } id)
                    candidates = candidates.Concat(await projection.CandidatesAsync(100, id, ct)).DistinctBy(c => c.Id).Take(200).ToArray();
            }
            catch (JsonException) { /* A malformed first mapping cannot create scope authority. */ }
        }
        var deferred = new List<SprintPlanDeferral>();
        var units = new List<(string RootId, SprintOutcomeBinding Binding, SprintActivationSnapshot Snapshot)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            SprintOutcomeBinding? binding = null;
            if (candidate.BindingJson is { Length: <= 32768 } json)
                try { binding = JsonSerializer.Deserialize<SprintOutcomeBinding>(json, SprintPlanJson.Options); }
                catch (JsonException) { /* Invalid bindings are recorded as deferred below. */ }
            if (binding is null)
            {
                deferred.Add(new(candidate.Id, "Needs approved outcome mapping and technical grooming."));
                continue;
            }
            try
            {
                SprintOutcomeAuthority.ValidateShape(candidate.Id, binding);
                if (binding.Reason == SprintMemberReason.Prerequisite) { deferred.Add(new(candidate.Id, "Prerequisite waits for its consuming approved outcome.")); continue; }
                var key = SprintPlanJson.Hash(new { binding.OutcomeId, Required = binding.RequiredTaskIds.Order(StringComparer.Ordinal), Criteria = binding.Criteria.OrderBy(c => c.Id) });
                if (!seen.Add(key)) continue;
                var snapshot = await validator.ValidateAsync([candidate.Id], ct, preview: true);
                units.Add((candidate.Id, binding, snapshot));
            }
            catch (InvalidOperationException ex) { deferred.Add(new(candidate.Id, ex.Message)); }
        }
        var fingerprint = SprintPlanJson.Hash(new {
            candidates,
            snapshots = units.Select(u => SprintActivationValidator.Fingerprint(u.Snapshot)),
            capacities = roleCaps?.OrderBy(p => p.Key), policy.MaxEngineeringTasks
        });
        if (_cacheKey == fingerprint && _cache is not null) { if (_cache.Snapshot is not null) _afterId = null; return _cache; }
        if (units.Count == 0) return Cache(new(null, deferred, "No complete approved outcome is ready within the bounded candidate window."));
        // Priority/age order comes from the bounded database projection. Within that first
        // goal, prefer a small useful unit; parallel coverage is a soft secondary benefit.
        _afterId = null;
        var first = units[0];
        var related = units.Where(u => u.Binding.OutcomeId == first.Binding.OutcomeId && u.Binding.Objective == first.Binding.Objective)
            .OrderBy(u => u.Snapshot.TaskIds.Count).ThenBy(u => u.Snapshot.TaskIds[0], StringComparer.Ordinal).ToList();
        var selected = related[0].Snapshot;
        var roots = new List<string> { related[0].RootId };
        foreach (var unit in related.Skip(1))
        {
            if (unit.Snapshot.TaskIds.All(selected.TaskIds.Contains)) continue;
            try
            {
                var expanded = await validator.ValidateAsync(roots.Append(unit.RootId).ToArray(), ct, preview: true);
                if (ParallelCoverage(expanded) > ParallelCoverage(selected))
                { selected = expanded; roots.Add(unit.RootId); }
            }
            catch (InvalidOperationException ex) { deferred.Add(new(unit.RootId, ex.Message)); }
        }
        var selectedIds = selected.TaskIds.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(c => !selectedIds.Contains(c.Id) && !deferred.Any(d => d.TaskId == c.Id)))
            deferred.Add(new(candidate.Id, "Deferred: outside the selected independently useful outcome or adds no useful parallel coverage."));
        var plan = selected.Plan! with { Deferred = deferred.OrderBy(d => d.TaskId, StringComparer.Ordinal).ToArray(), InputFingerprint = fingerprint, Hash = "" };
        selected = selected with { Plan = plan with { Hash = SprintPlan.ComputeHash(plan) } };
        return Cache(new(selected, deferred, "Smallest complete outcome, with natural independent work where configured role capacity benefits."));

        SprintPlanBuildResult Cache(SprintPlanBuildResult result) { _cacheKey = fingerprint; _cache = result; return result; }
    }

    private int ParallelCoverage(SprintActivationSnapshot snapshot)
    {
        var tasks = snapshot.TaskIds.ToHashSet(StringComparer.Ordinal);
        var dependent = (snapshot.Dependencies ?? []).Where(e => tasks.Contains(e.BlockerId)).Select(e => e.BlockedId).ToHashSet(StringComparer.Ordinal);
        // Watch-lane QA/review capacity cannot manufacture another independently
        // implementable task or its required merged publication evidence.
        return snapshot.Issues.Where(i => tasks.Contains(i.Id) && !dependent.Contains(i.Id)
                && RoleAgentRegistry.FromTaskType(i.Type) is AgentType.CoreDev or AgentType.ClientDev)
            .GroupBy(i => RoleAgentRegistry.FromTaskType(i.Type).ToString().ToLowerInvariant())
            .Sum(group => Math.Min(group.Count(), Math.Max(0, DefaultProjectRoles.MaxFor(roleCaps is null ? [] : new Dictionary<string,int>(roleCaps), group.Key))));
    }
}
