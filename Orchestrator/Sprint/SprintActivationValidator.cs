using System.Security.Cryptography;
using System.Text.Json;
using Forge.Core;
using Forge.Orchestrator.Sprint;

namespace Forge.Orchestrator;

/// <summary>One policy for automatic assembly and operator proposal activation.
/// Captured rows are rechecked inside the membership transaction before activation is published.</summary>
internal sealed class SprintActivationValidator(
    IIssueStore issues, ISprintStore sprints, ISpecStore specs, string projectId, StageGates? gates)
{
    public async Task<SprintActivationSnapshot> ValidateAsync(IReadOnlyList<string> taskIds, CancellationToken ct, bool activating = true)
    {
        if (activating && gates is not null && await gates.IsHeldAsync(StageGates.Sprint, ct))
            throw new InvalidOperationException("Sprint assembly is held by the operator.");
        if (taskIds.Count == 0 || taskIds.Distinct(StringComparer.Ordinal).Count() != taskIds.Count)
            throw new InvalidOperationException("Select one or more distinct eligible tasks.");
        if (activating && await sprints.GetActiveAsync(ct) is not null)
            throw new InvalidOperationException("An active sprint already exists.");
        var all = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var eligible = (await SprintAssembler.ListEligibleAsync(issues, sprints, ct)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var existing = new Dictionary<string, IssueRecord>(StringComparer.Ordinal);
        if (!activating && await sprints.GetActiveAsync(ct) is { } active)
            foreach (var id in await sprints.GetIssueIdsAsync(active.Id, ct))
                if (all.TryGetValue(id, out var row) && !AgentTaskTypes.IsContainer(row.Type) && row.Type != AgentTaskTypes.PrWatch)
                    existing[id] = row;
        var incoming = await issues.IncomingDependenciesAsync(eligible.Concat(existing.Keys).ToArray(), ct);
        return await ValidateLoadedAsync(taskIds, all, eligible, new Dictionary<string, SpecRecord?>(), incoming, existing, ct);
    }

    public async Task<IReadOnlyDictionary<string, SprintActivationSnapshot>> PreviewSnapshotsAsync(
        IEnumerable<string> rankedIds, int count, CancellationToken ct)
    {
        var all = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var eligible = (await SprintAssembler.ListEligibleAsync(issues, sprints, ct)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var specCache = new Dictionary<string, SpecRecord?>(StringComparer.Ordinal);
        var incoming = await issues.IncomingDependenciesAsync(eligible.ToArray(), ct);
        var result = new Dictionary<string, SprintActivationSnapshot>(StringComparer.Ordinal);
        foreach (var id in rankedIds)
        {
            if (result.Count >= Math.Max(0, count)) break;
            if (!eligible.Contains(id)) continue;
            try
            {
                result[id] = await ValidateLoadedAsync([id], all, eligible, specCache, incoming,
                    new Dictionary<string, IssueRecord>(), ct);
            }
            catch (InvalidOperationException)
            {
                // Invalid ancestry/ownership is not a selectable suggestion.
                continue;
            }
        }
        return result;
    }

    private async Task<SprintActivationSnapshot> ValidateLoadedAsync(IReadOnlyList<string> taskIds,
        IReadOnlyDictionary<string, IssueRecord> all, IReadOnlySet<string> eligible,
        IDictionary<string, SpecRecord?> specCache, IReadOnlyList<IssueEdge> incoming,
        IReadOnlyDictionary<string, IssueRecord> existing, CancellationToken ct)
    {
        var edgesByTarget = incoming.ToLookup(e => e.BlockedId, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new List<SprintDependencySnapshot>();
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("Sprint dependency graph contains a cycle.");
            if (visiting.Count + visited.Count > 1000)
                throw new InvalidOperationException("Dependency closure exceeds the 1000-task validation limit.");
            if (!existing.ContainsKey(id))
            {
                if (!eligible.Contains(id))
                    throw new InvalidOperationException($"Prerequisite {id} is missing, unavailable, or not groomed for this sprint.");
                selected.Add(id);
            }
            foreach (var edge in edgesByTarget[id])
            {
                var status = all.TryGetValue(edge.BlockerId, out var blocker) ? blocker.Status : (IssueStatus?)null;
                dependencies.Add(new SprintDependencySnapshot(edge.BlockerId, id, status));
                if (status is IssueStatus.Completed or IssueStatus.Closed) continue;
                if (status is null or IssueStatus.Failed or IssueStatus.Blocked)
                    throw new InvalidOperationException($"Prerequisite {edge.BlockerId} needs operator resolution before sprint planning.");
                Visit(edge.BlockerId);
            }
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var id in taskIds) Visit(id);
        // Amend the unfinished sprint as one dependency-closed unit. Multiple
        // newly discovered prerequisites must join together, not deadlock each
        // other's single-task amendments at the transaction boundary.
        foreach (var row in existing.Values.Where(row => row.Status is not (IssueStatus.Completed or IssueStatus.Closed)))
            Visit(row.Id);
        // Terminal members remain observations, never new planning roots.
        foreach (var id in existing.Keys.Where(id => !visited.Contains(id)))
            foreach (var edge in edgesByTarget[id])
                dependencies.Add(new SprintDependencySnapshot(edge.BlockerId, id,
                    all.TryGetValue(edge.BlockerId, out var blocker) ? blocker.Status : null));
        var rows = new Dictionary<string, IssueRecord>(StringComparer.Ordinal);
        var specRows = new Dictionary<string, SpecRecord>(StringComparer.Ordinal);
        foreach (var id in selected)
        {
            if (!eligible.Contains(id) || !all.TryGetValue(id, out var task))
                throw new InvalidOperationException($"Task {id} is not eligible for sprint activation.");
            rows[id] = task;
            var current = task;
            var ancestors = new HashSet<string>(StringComparer.Ordinal) { id };
            while (current.ParentIssueId is { } parentId)
            {
                if (!ancestors.Add(parentId))
                    throw new InvalidOperationException($"Task {id} has a cyclic parent chain.");
                if (all.TryGetValue(parentId, out var parent))
                {
                    rows[parentId] = parent;
                    current = parent;
                    continue;
                }
                if (!specCache.TryGetValue(parentId, out var spec))
                    specCache[parentId] = spec = await specs.GetAsync(parentId, ct);
                if (spec is null || spec.Status != SpecStatus.Groomed || !string.Equals(spec.ProjectId, projectId, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Task {id} requires a groomed specification owned by project {projectId}.");
                specRows[spec.Id] = spec;
                break;
            }
        }
        return new SprintActivationSnapshot(rows.Values.ToArray(), specRows.Values.ToArray(), selected.ToArray(),
            Dependencies: dependencies, ExistingSprintMembers: existing.Count == 0 ? null : existing.Values.ToArray());
    }

    public static string Fingerprint(SprintActivationSnapshot snapshot) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new {
            issues = snapshot.Issues.OrderBy(i => i.Id, StringComparer.Ordinal),
            specs = snapshot.Specs.OrderBy(s => s.Id, StringComparer.Ordinal),
            tasks = snapshot.TaskIds.OrderBy(id => id, StringComparer.Ordinal),
            dependencies = snapshot.Dependencies?.OrderBy(e => e.BlockedId, StringComparer.Ordinal).ThenBy(e => e.BlockerId, StringComparer.Ordinal),
            existing = snapshot.ExistingSprintMembers?.OrderBy(i => i.Id, StringComparer.Ordinal)
        })));
}
