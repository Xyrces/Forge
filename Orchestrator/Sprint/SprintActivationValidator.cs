using System.Security.Cryptography;
using System.Text.Json;
using Forge.Core;
using Forge.Configuration;

namespace Forge.Orchestrator;

/// <summary>Bounded, exact approved-scope validation shared by every activation and amendment path.</summary>
internal sealed class SprintActivationValidator(
    IIssueStore issues, ISprintStore sprints, ISpecStore specs, string projectId, StageGates? gates,
    SprintPlanningOptions? options = null, IReadOnlyDictionary<string,int>? roleCaps = null)
{
    public async Task<SprintActivationSnapshot> ValidateAsync(IReadOnlyList<string> taskIds, CancellationToken ct, bool activating = true, bool preview = false)
    {
        if (activating && !preview && gates is not null && await gates.IsHeldAsync(StageGates.Sprint, ct))
            throw new InvalidOperationException("Sprint assembly is held by the operator.");
        if (taskIds.Count == 0 || taskIds.Distinct(StringComparer.Ordinal).Count() != taskIds.Count)
            throw new InvalidOperationException("Select one or more distinct eligible tasks.");
        var active = await sprints.GetActiveAsync(ct);
        if (activating && !preview && active is not null) throw new InvalidOperationException("An active sprint already exists.");
        var occupied = activating && active is not null ? (await sprints.GetIssueIdsAsync(active.Id, ct)).ToHashSet(StringComparer.Ordinal) : [];
        var concrete = issues as IssueStore ?? throw new InvalidOperationException("Approved scope validation requires the owning project store.");
        var authority = new SprintOutcomeAuthority(concrete);
        var all = new Dictionary<string, IssueRecord>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        async Task<IssueRecord?> Load(string id)
        {
            if (all.TryGetValue(id, out var value)) return value;
            if (missing.Contains(id)) return null;
            if (all.Count + missing.Count >= 1000) throw new InvalidOperationException("Planning context exceeds its 1000-row safety bound; decompose the outcome.");
            value = await issues.GetAsync(id, ct);
            if (value is null) missing.Add(id); else all[id] = value;
            return value;
        }
        async Task<bool> Satisfied(IssueRecord row)
        {
            if (row.GetMetadata("closeReferenceIssueId") is { } reference) await Load(reference);
            return IssueResolution.IsSatisfied(row, all);
        }
        var existing = new Dictionary<string, IssueRecord>(StringComparer.Ordinal);
        SprintPlan? previous = null;
        var approvedExisting = new HashSet<string>(StringComparer.Ordinal);
        if (!activating)
        {
            if (active is null) throw new InvalidOperationException("No active sprint to amend.");
            previous = await new SprintProposalAuditStore(concrete.Db).GetPlanForSprintAsync(active.Id, ct);
            if (previous is null) throw new InvalidOperationException("Active sprint has no approved outcome evidence; operator scope repair is required.");
            foreach (var id in await sprints.GetIssueIdsAsync(active.Id, ct))
                if (await Load(id) is { } row && !AgentTaskTypes.IsContainer(row.Type) && row.Type != AgentTaskTypes.PrWatch) existing[id] = row;
            approvedExisting.UnionWith(previous.Members.Select(m => m.TaskId));
            if (!approvedExisting.IsSubsetOf(existing.Keys))
                throw new InvalidOperationException("Active sprint membership differs from its approved plan; explicit scope repair is required.");
            foreach (var member in previous.Members)
                if (SprintPlanJson.ReadBinding(existing[member.TaskId]) is not { } binding || member.BindingHash != SprintPlanJson.Hash(binding))
                    throw new InvalidOperationException("An active member's outcome mapping changed; explicit scope repair is required.");
        }
        var rows = new Dictionary<string, IssueRecord>(StringComparer.Ordinal);
        var specRows = new Dictionary<string, SpecRecord>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, SprintOutcomeBinding>(StringComparer.Ordinal);
        var scopeCache = new HashSet<SprintScopeReference>();
        async Task<SprintOutcomeBinding> Binding(IssueRecord row)
        {
            if (bindings.TryGetValue(row.Id, out var cached)) return cached;
            var binding = SprintPlanJson.ReadBinding(row) ?? throw new InvalidOperationException($"Task {row.Id} needs an explicit approved outcome mapping from grooming.");
            SprintOutcomeAuthority.ValidateShape(row.Id, binding);
            foreach (var scope in binding.Criteria.Select(c => c.Scope).Append(binding.Authority))
                if (scopeCache.Add(scope)) await authority.ValidateAsync(scope, ct);
            bindings[row.Id] = binding;
            return binding;
        }
        var roots = new HashSet<string>(taskIds, StringComparer.Ordinal);
        string? outcomeId = null;
        string? objective = null;
        var criteria = new Dictionary<string, SprintCriterion>(StringComparer.Ordinal);
        if (previous is not null) foreach (var criterion in previous.Criteria) criteria.Add(criterion.Id, criterion);
        // A delivery unit is all-or-nothing. Never truncate a criterion's required tasks to fit a cap.
        foreach (var id in taskIds)
        {
            var row = await Load(id) ?? throw new InvalidOperationException($"Task {id} is missing.");
            var binding = await Binding(row);
            if (previous is null)
            {
                outcomeId ??= binding.OutcomeId; objective ??= binding.Objective;
                if (outcomeId != binding.OutcomeId || objective != binding.Objective)
                    throw new InvalidOperationException("Selected work does not describe one explicitly mapped outcome.");
                foreach (var criterion in binding.Criteria)
                {
                    if (criteria.TryGetValue(criterion.Id, out var old) && old != criterion)
                        throw new InvalidOperationException("Conflicting acceptance criterion definitions.");
                    criteria[criterion.Id] = criterion;
                }
                foreach (var required in binding.RequiredTaskIds) roots.Add(required);
            }
            else if (!binding.CriterionIds.All(criteria.ContainsKey))
                throw new InvalidOperationException("An amendment cannot expand or replace the active approved outcome.");
        }
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new List<SprintDependencySnapshot>();
        async Task Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("Sprint dependency graph contains a cycle.");
            var row = await Load(id) ?? throw new InvalidOperationException($"Prerequisite {id} is missing.");
            if (!approvedExisting.Contains(id))
            {
                if (occupied.Contains(id) || row.Status != IssueStatus.Pending || AgentTaskTypes.IsContainer(row.Type) || row.Type == AgentTaskTypes.PrWatch)
                    throw new InvalidOperationException($"Prerequisite {id} is unavailable or needs operator resolution.");
                var role = Forge.Agents.RoleAgentRegistry.FromTaskType(row.Type).ToString().ToLowerInvariant();
                if (roleCaps is not null && roleCaps.Any(p => string.Equals(p.Key,role,StringComparison.OrdinalIgnoreCase) && p.Value <= 0))
                    throw new InvalidOperationException($"Required role {role} has no configured capacity.");
                selected.Add(id);
                if (selected.Count + approvedExisting.Count > Math.Clamp(options?.MaxEngineeringTasks ?? 10, 1, 1000))
                    throw new InvalidOperationException("The complete outcome and prerequisites exceed the sprint ceiling; grooming must identify a smaller independently useful outcome.");
                rows[id] = row;
                var binding = await Binding(row);
                if (roots.Contains(id) && previous is null && (binding.OutcomeId != outcomeId || binding.Objective != objective))
                    throw new InvalidOperationException("A required delivery member belongs to another outcome.");
                var current = row;
                var parents = new HashSet<string> { id };
                var foundSpec = false;
                while (current.ParentIssueId is { } parentId)
                {
                    if (!parents.Add(parentId)) throw new InvalidOperationException("Cyclic task ancestry.");
                    if (await Load(parentId) is { } parent) { rows[parentId] = parent; current = parent; continue; }
                    var spec = await specs.GetAsync(parentId, ct);
                    if (spec is null || spec.ProjectId != projectId || spec.Status != SpecStatus.Groomed || binding.Authority.SpecId != spec.Id)
                        throw new InvalidOperationException($"Task {id} requires its own approved, groomed project specification.");
                    specRows[spec.Id] = spec; foundSpec = true; break;
                }
                if (!foundSpec && !string.Equals(row.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Task {id} is not technically groomed.");
            }
            foreach (var edge in await issues.IncomingDependenciesAsync([id], ct))
            {
                var blocker = await Load(edge.BlockerId);
                var satisfied = blocker is not null && await Satisfied(blocker);
                dependencies.Add(new SprintDependencySnapshot(edge.BlockerId, id, blocker?.Status, blocker?.MetadataJson, satisfied));
                if (satisfied) continue;
                if (blocker is null || blocker.Status is IssueStatus.Failed or IssueStatus.Blocked or IssueStatus.Closed)
                    throw new InvalidOperationException($"Prerequisite {edge.BlockerId} requires operator resolution.");
                await Visit(edge.BlockerId);
            }
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in roots) await Visit(id);
        foreach (var row in existing.Values.Where(r => approvedExisting.Contains(r.Id))) if (!await Satisfied(row)) await Visit(row.Id);
        foreach (var row in existing.Values.Where(r => !visited.Contains(r.Id)))
            foreach (var edge in await issues.IncomingDependenciesAsync([row.Id], ct))
            {
                var blocker = await Load(edge.BlockerId);
                dependencies.Add(new SprintDependencySnapshot(edge.BlockerId, row.Id, blocker?.Status, blocker?.MetadataJson,
                    blocker is not null && await Satisfied(blocker)));
            }
        if (existing.Keys.Any(id => !approvedExisting.Contains(id) && !selected.Contains(id)))
            throw new InvalidOperationException("Visibility-only members require complete groomed prerequisite amendments before plan publication.");
        // Each included unit must have complete, consistent member mappings.
        if (previous is null)
            foreach (var id in roots)
            {
                var b = bindings[id];
                if (b.RequiredTaskIds.Any(required => !selected.Contains(required)) || b.Criteria.Any(c => !criteria.TryGetValue(c.Id, out var expected) || expected != c))
                    throw new InvalidOperationException("Delivery-unit membership or criteria are incomplete.");
            }
        var members = previous?.Members.ToList() ?? [];
        foreach (var id in selected.Order(StringComparer.Ordinal))
        {
            var binding = bindings[id];
            if (binding.RequiredTaskIds.Any(required => !selected.Contains(required) && !approvedExisting.Contains(required)))
                throw new InvalidOperationException($"Prerequisite unit for {id} is incomplete; grooming must wire its complete necessary closure.");
            var mapped = binding.CriterionIds.Where(criteria.ContainsKey).Order(StringComparer.Ordinal).ToArray();
            // Prerequisites must explicitly bind to a selected criterion too, not merely share a title or file.
            if (mapped.Length == 0) throw new InvalidOperationException($"Prerequisite {id} needs explicit mapping to the selected acceptance criteria.");
            if (mapped.Any(key => !binding.Criteria.Contains(criteria[key])))
                throw new InvalidOperationException("Acceptance criterion identity is reused with different scope or evidence.");
            members.Add(new(id, mapped, roots.Contains(id) && previous is null ? binding.Reason : SprintMemberReason.Prerequisite,
                SprintPlanJson.Hash(binding)));
        }
        if (criteria.Keys.Any(id => !members.Any(m => m.CriterionIds.Contains(id))))
            throw new InvalidOperationException("Acceptance criteria lack implementing members.");
        var snapshot = new SprintActivationSnapshot(rows.Values.ToArray(), specRows.Values.ToArray(), selected.Order(StringComparer.Ordinal).ToArray(),
            Dependencies: dependencies, ExistingSprintMembers: existing.Count == 0 ? null : existing.Values.ToArray());
        var plan = new SprintPlan(1, projectId, previous?.Objective ?? string.Join("; ", criteria.Values.OrderBy(c => c.Id).Select(c => c.Text)),
            criteria.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray(), members.OrderBy(m => m.TaskId, StringComparer.Ordinal).ToArray(), [], Fingerprint(snapshot), "");
        return snapshot with { Plan = plan with { Hash = SprintPlan.ComputeHash(plan) } };
    }

    public async Task<IReadOnlyDictionary<string, SprintActivationSnapshot>> PreviewSnapshotsAsync(IEnumerable<string> rankedIds, int count, CancellationToken ct)
    {
        var result = new Dictionary<string, SprintActivationSnapshot>();
        foreach (var id in rankedIds.Take(100))
        {
            if (result.Count >= count) break;
            try { result[id] = await ValidateAsync([id], ct); }
            catch (InvalidOperationException) { /* Omitted candidates retain their original queue state. */ }
        }
        return result;
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
