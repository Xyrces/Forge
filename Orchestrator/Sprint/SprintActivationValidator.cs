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
        return await ValidateLoadedAsync(taskIds, all, eligible, new Dictionary<string, SpecRecord?>(), ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> PreviewFingerprintsAsync(
        IEnumerable<string> rankedIds, int count, CancellationToken ct)
    {
        var all = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var eligible = (await SprintAssembler.ListEligibleAsync(issues, sprints, ct)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var specCache = new Dictionary<string, SpecRecord?>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in rankedIds)
        {
            if (result.Count >= Math.Max(0, count)) break;
            if (!eligible.Contains(id)) continue;
            try
            {
                result[id] = Fingerprint(await ValidateLoadedAsync([id], all, eligible, specCache, ct));
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
        IDictionary<string, SpecRecord?> specCache, CancellationToken ct)
    {
        var rows = new Dictionary<string, IssueRecord>(StringComparer.Ordinal);
        var specRows = new Dictionary<string, SpecRecord>(StringComparer.Ordinal);
        foreach (var id in taskIds)
        {
            if (!eligible.Contains(id) || !all.TryGetValue(id, out var task))
                throw new InvalidOperationException($"Task {id} is not eligible for sprint activation.");
            rows[id] = task;
            var current = task;
            var visited = new HashSet<string>(StringComparer.Ordinal) { id };
            while (current.ParentIssueId is { } parentId)
            {
                if (!visited.Add(parentId))
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
        return new SprintActivationSnapshot(rows.Values.ToArray(), specRows.Values.ToArray(), taskIds);
    }

    public static string Fingerprint(SprintActivationSnapshot snapshot) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new {
            issues = snapshot.Issues.OrderBy(i => i.Id, StringComparer.Ordinal),
            specs = snapshot.Specs.OrderBy(s => s.Id, StringComparer.Ordinal),
            tasks = snapshot.TaskIds.OrderBy(id => id, StringComparer.Ordinal)
        })));
}
