using System.Data;
using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;
using static Forge.Core.Initiatives.InitiativeDb;

namespace Forge.Core;

public sealed record MoveOperatorChecksRequest(int SpecVersion, long GroomRunId, string PreviewHash);
public sealed record OperatorAcceptanceRepairAvailability(bool Eligible, string? Reason, OperatorCheckAcceptancePreview? Preview);
public sealed record OperatorCheckAcceptancePreview(string ProjectId, string SpecId, int SpecVersion, long GroomRunId,
    string PreviewHash, SprintScopeReference Authority, string OutcomeId, string Objective,
    IReadOnlyList<IssueRecord> SourceRows, IReadOnlyList<IssueRecord> TargetRows,
    IReadOnlyList<SprintCriterion> TransferredCriteria, IReadOnlyDictionary<string, SprintOutcomeBinding> TargetBindings);
public sealed record OperatorAcceptanceRepairReceipt(OperatorCheckAcceptancePreview Preview, string Actor, DateTime AppliedAt);

/// <summary>Explicit, bounded correction of observation-only QA rows before any engineering admission.</summary>
public sealed class OperatorAcceptanceRepair(IssueStore issues)
{
    private string T(string name) => issues.Db.Dialect.Table(name);
    private static string Key(string specId, int version, long run) => $"operator-acceptance-repair/{specId}/{version}/{run}";
    private sealed record Edge(string Blocker, string Blocked, string Kind, string CreatedAt);

    public async Task<OperatorAcceptanceRepairAvailability> PreviewAsync(string specId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await issues.Db.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var preview = await BuildAsync(connection, tx, specId, null, null, ct);
            await tx.CommitAsync(ct);
            return new(true, null, preview);
        }
        catch (InvalidOperationException ex) { return new(false, ex.Message, null); }
        catch (JsonException) { return new(false, "Malformed stored outcome evidence requires investigation.", null); }
    }

    public async Task<OperatorCheckAcceptancePreview> PreviewAsync(string specId, int specVersion, long groomRunId, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var preview = await BuildAsync(connection, tx, specId, specVersion, groomRunId, ct);
        await tx.CommitAsync(ct);
        return preview;
    }

    public async Task<OperatorAcceptanceRepairReceipt> ApplyAsync(string specId, MoveOperatorChecksRequest request, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || actor.Any(char.IsControl))
            throw new ArgumentException("An honest operator identity is required.");
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAsync(connection, tx, issues.Db.Dialect, ct);
        var key = Key(specId, request.SpecVersion, request.GroomRunId);
        using (var read = Command(connection, tx, $"SELECT body FROM {T("memory")} WHERE [key]=@key", ("@key", key)))
        {
            if (await read.ExecuteScalarAsync(ct) is string json)
            {
                var prior = JsonSerializer.Deserialize<OperatorAcceptanceRepairReceipt>(json, SprintPlanJson.Options)
                    ?? throw new InvalidOperationException("Repair receipt is invalid.");
                if (prior.Preview.PreviewHash != request.PreviewHash || prior.Actor != actor
                    || prior.Preview.ProjectId != issues.ProjectId || prior.Preview.SpecId != specId)
                    throw new InvalidOperationException("This repair was already applied with different coordinates.");
                await tx.CommitAsync(ct);
                return prior;
            }
        }
        var preview = await BuildAsync(connection, tx, specId, request.SpecVersion, request.GroomRunId, ct);
        if (preview.PreviewHash != request.PreviewHash) throw new InvalidOperationException("Repair preview changed; reload before applying.");
        var receipt = new OperatorAcceptanceRepairReceipt(preview, actor, DateTime.UtcNow);
        foreach (var target in preview.TargetRows)
            await issues.ApplyOperatorAcceptanceRepairAsync(connection, tx, target, new Dictionary<string, object>
            {
                [SprintPlanJson.MetadataKey] = JsonSerializer.SerializeToElement(preview.TargetBindings[target.Id], SprintPlanJson.Options),
                ["operatorAcceptanceRepair"] = key
            }, false, actor, ct);
        foreach (var source in preview.SourceRows)
            await issues.ApplyOperatorAcceptanceRepairAsync(connection, tx, source, new Dictionary<string, object>
            {
                ["closeDisposition"] = nameof(CloseDispositionKind.Cancelled), ["closeEvidence"] = "", ["closeReferenceIssueId"] = "",
                ["closeReason"] = "Observation-only engineering representation retired; exact criteria remain pending as operator acceptance on the delivery outcome.",
                ["operatorAcceptanceRepair"] = key
            }, true, actor, ct);
        using (var write = Command(connection, tx, $"INSERT INTO {T("memory")}(ts,[key],body,ttl_days) VALUES(@now,@key,@body,NULL)",
            ("@now", Now()), ("@key", key), ("@body", SprintPlanJson.Serialize(receipt))))
            await write.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return receipt;
    }

    private async Task<OperatorCheckAcceptancePreview> BuildAsync(DbConnection c, DbTransaction tx, string specId, int? expectedVersion, long? expectedRun, CancellationToken ct)
    {
        int version; string specUpdated; string versionCreated;
        using (var read = Command(c, tx, $"SELECT s.current_version,s.updated_at,v.created_at FROM {T("spec")} s JOIN {T("spec_version")} v ON v.spec_id=s.id AND v.version=s.current_version WHERE s.id=@spec AND s.project_id=@project AND s.status='Groomed'", ("@spec", specId), ("@project", issues.ProjectId)))
        {
            await using var r = await read.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Only a Groomed specification in its owning project can be repaired.");
            version = Convert.ToInt32(r.GetValue(0)); specUpdated = r.GetString(1); versionCreated = r.GetString(2);
        }
        if (expectedVersion is not null && expectedVersion != version) throw new InvalidOperationException("Specification version changed.");
        long runId; string runStarted;
        using (var read = Command(c, tx, $"SELECT {issues.Db.Dialect.Top(1)}id,status,ts FROM {T("issue_groomer_run")} WHERE spec_id=@spec ORDER BY id DESC{issues.Db.Dialect.Limit(1)}", ("@spec", specId)))
        {
            await using var r = await read.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.GetString(1) != "succeeded") throw new InvalidOperationException("The latest owning-store grooming run must have succeeded.");
            runId = Convert.ToInt64(r.GetValue(0)); runStarted = r.GetString(2);
        }
        if (IssueStore.ParseTime(versionCreated) > IssueStore.ParseTime(runStarted))
            throw new InvalidOperationException("The current specification version was not present when this grooming run started.");
        if (expectedRun is not null && expectedRun != runId) throw new InvalidOperationException("Grooming attempt changed.");
        var rows = new List<IssueRecord>();
        using (var read = Command(c, tx, $"SELECT {issues.Db.Dialect.Top(101)}id,short_id,type,title,description,status,priority,assignee,created_at,updated_at,closed_at,metadata_json,parent_issue_id,dispatch_checkpoint,checkpoint_at,recovery_attempts FROM {T("issue")} WHERE parent_issue_id=@spec OR parent_issue_id IN(SELECT id FROM {T("issue")} WHERE parent_issue_id=@spec) ORDER BY id{issues.Db.Dialect.Limit(101)}", ("@spec", specId)))
        {
            await using var r = await read.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(IssueStore.ReadIssue(r));
        }
        if (rows.Count is 0 or > 100) throw new InvalidOperationException("Repair requires a bounded intact decomposition of at most 100 rows.");
        var stories = rows.Where(r => r.Type == "story" && r.ParentIssueId == specId).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var tasks = rows.Where(r => r.Type != "story").ToArray();
        using (var children = Command(c, tx, $"SELECT COUNT(*) FROM {T("spec")} WHERE parent_spec_id=@spec", ("@spec", specId)))
            if (Convert.ToInt64(await children.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("Child specifications require explicit grooming revision.");
        if (tasks.Length == 0 || rows.Any(r => r.Status != IssueStatus.Pending || r.Assignee is not null || r.DispatchCheckpoint is not null || r.CheckpointAt is not null || r.RecoveryAttempts != 0 || r.ClosedAt is not null)
            || rows.Any(r => r.Type == "story" ? !stories.Contains(r.Id) : AgentTaskTypes.IsContainer(r.Type) || r.Type == AgentTaskTypes.PrWatch || r.ParentIssueId is null || !stories.Contains(r.ParentIssueId)))
            throw new InvalidOperationException("Only untouched Pending story/task rows can be repaired.");
        var ids = rows.Select(r => r.Id).ToArray();
        var parameters = ids.Select((id, i) => (Name: "@i" + i, Value: (object?)id)).ToArray();
        var list = string.Join(',', parameters.Select(p => p.Name));
        using (var deeper = Command(c, tx, $"SELECT COUNT(*) FROM {T("issue")} WHERE parent_issue_id IN({list}) AND parent_issue_id NOT IN(SELECT id FROM {T("issue")} WHERE parent_issue_id=@spec AND type='story')", [.. parameters, ("@spec", specId)]))
            if (Convert.ToInt64(await deeper.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("Nested work outside the supported story/task shape prevents repair.");
        foreach (var row in rows)
        {
            using var metadata = JsonDocument.Parse(row.MetadataJson);
            if (metadata.RootElement.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Null && new[] { "prNumber", "branch", "worktreePath", "dispatchId", "resumeDispatchId", "state", "reviewSha", "mergedSha", "agentRunId" }.Contains(p.Name)))
                throw new InvalidOperationException("Execution or publication metadata prevents this repair.");
        }
        using (var history = Command(c, tx, $"SELECT COUNT(*) FROM {T("issue_event")} WHERE issue_id IN({list}) AND kind NOT IN('created','grooming_metadata','dep_added','dep_removed')", parameters))
            if (Convert.ToInt64(await history.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("Task history contains prior lifecycle activity.");
        using (var membership = Command(c, tx, $"SELECT COUNT(*) FROM {T("sprint_issue")} WHERE issue_id IN({list})", parameters))
            if (Convert.ToInt64(await membership.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("Prior sprint membership prevents repair.");
        using (var runs = Command(c, tx, $"SELECT COUNT(*) FROM {T("agent_run")} WHERE task_id IN({list})", parameters))
            if (Convert.ToInt64(await runs.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("Prior agent execution prevents repair.");
        // Keep this selective and provider-native: do not scan or deserialize an unbounded project audit history.
        var auditMembers = issues.Db.Provider == ForgeDbProvider.SqlServer
            ? $"SELECT 1 FROM OPENJSON(a.selected_task_ids_json) j WHERE j.[value] IN({list})"
            : $"SELECT 1 FROM json_each(a.selected_task_ids_json) j WHERE j.value IN({list})";
        using (var audits = Command(c, tx, $"SELECT COUNT(*) FROM {T("sprint_proposal_audit")} a WHERE a.committed_sprint_id IS NOT NULL AND EXISTS({auditMembers})", parameters))
            if (Convert.ToInt64(await audits.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("Committed sprint history prevents repair.");
        var edges = new List<Edge>();
        using (var deps = Command(c, tx, $"SELECT blocker_id,blocked_id,kind,created_at FROM {T("issue_dep")} WHERE blocker_id IN({list}) OR blocked_id IN({list}) ORDER BY blocker_id,blocked_id,kind", parameters))
        {
            await using var r = await deps.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (edges.Count == 1000) throw new InvalidOperationException("Dependency review exceeds its 1000-edge bound.");
                edges.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            }
        }
        var bindings = tasks.ToDictionary(r => r.Id, r => SprintPlanJson.ReadBinding(r) ?? throw new InvalidOperationException("Every task must have an explicit outcome mapping."));
        foreach (var (id, binding) in bindings)
        {
            // Sources intentionally predate the engineering-phase guard; validate shape without pretending they implement code.
            if (binding.Version != 1 || binding.Criteria is not { Count: > 0 and <= 20 } || binding.RequiredTaskIds is not { Count: > 0 and <= 100 }
                || binding.CriterionIds is not { Count: > 0 } || !binding.RequiredTaskIds.Contains(id) || binding.Authority is null
                || binding.Criteria.Any(x => x is null || x.Scope is null || string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 128 || string.IsNullOrWhiteSpace(x.Text) || x.Text.Length > 2000)
                || binding.Criteria.Select(x => x.Id).Distinct().Count() != binding.Criteria.Count
                || binding.RequiredTaskIds.Distinct().Count() != binding.RequiredTaskIds.Count || !Enum.IsDefined(binding.Reason)
                || binding.CriterionIds.Any(x => !binding.Criteria.Any(y => y.Id == x)) || string.IsNullOrWhiteSpace(binding.OutcomeId) || binding.OutcomeId.Length > 128 || string.IsNullOrWhiteSpace(binding.Objective) || binding.Objective.Length > 1000)
                throw new InvalidOperationException("Malformed outcome mapping cannot be repaired deterministically.");
            foreach (var scope in binding.Criteria.Select(x => x.Scope).Append(binding.Authority))
                await new SprintOutcomeAuthority(issues).ValidateAsync(c, tx, scope, ct);
        }
        var sources = tasks.Where(r => r.Type == "qa" && bindings[r.Id].Criteria.All(x => x.EvidenceKind == SprintEvidenceKind.OperatorObserved)).ToArray();
        var targets = tasks.Except(sources).ToArray();
        if (sources.Length == 0 || targets.Length is 0 or > 10) throw new InvalidOperationException("One bounded implementation unit and observation-only QA rows are required.");
        var sourceIds = sources.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var targetIds = targets.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var first = bindings[targets[0].Id];
        var authority = first.Authority;
        bool SameApproval(SprintScopeReference scope) => scope.SpecId == specId && scope.SpecId == authority.SpecId
            && scope.ProposalId == authority.ProposalId && scope.Revision == authority.Revision && scope.Hash == authority.Hash;
        if (bindings.Values.Any(b => !SameApproval(b.Authority) || b.Criteria.Any(x => !SameApproval(x.Scope))))
            throw new InvalidOperationException("All criteria must retain the same exact approved specification scope.");
        foreach (var target in targets)
        {
            var b = bindings[target.Id];
            SprintOutcomeAuthority.ValidateShape(target.Id, b);
            if (b.OutcomeId != first.OutcomeId || b.Objective != first.Objective || SprintPlanJson.Serialize(b.Criteria) != SprintPlanJson.Serialize(first.Criteria)
                || !targetIds.SetEquals(b.RequiredTaskIds) || !b.Criteria.Any(x => b.CriterionIds.Contains(x.Id) && x.EvidenceKind == SprintEvidenceKind.MergedAndVerified))
                throw new InvalidOperationException("The destination must be one complete unambiguous implementation unit.");
        }
        if (first.Criteria.Any(criterion => !targets.Any(target => bindings[target.Id].CriterionIds.Contains(criterion.Id))))
            throw new InvalidOperationException("Implementation criteria have incomplete task coverage.");
        foreach (var source in sources)
        {
            var b = bindings[source.Id];
            var peers = sources.Where(x => bindings[x.Id].OutcomeId == b.OutcomeId).Select(x => bindings[x.Id]).ToArray();
            if (peers.Any(peer => peer.Objective != b.Objective || SprintPlanJson.Serialize(peer.Criteria) != SprintPlanJson.Serialize(b.Criteria))
                || b.Criteria.Any(criterion => !peers.Any(peer => peer.CriterionIds.Contains(criterion.Id))))
                throw new InvalidOperationException("Observation criteria are not one consistently mapped unit.");
            if (b.RequiredTaskIds.Any(id => !sourceIds.Contains(id)) || b.RequiredTaskIds.Any(id => bindings[id].OutcomeId != b.OutcomeId)
                || !sources.Where(x => bindings[x.Id].OutcomeId == b.OutcomeId).Select(x => x.Id).ToHashSet().SetEquals(b.RequiredTaskIds))
                throw new InvalidOperationException("Observation units must be complete and contain no hidden engineering members.");
            var incoming = edges.Where(e => e.Blocked == source.Id && e.Kind == "blocks").ToArray();
            if (incoming.Length == 0 || incoming.Any(e => !targetIds.Contains(e.Blocker)) || edges.Any(e => e.Blocker == source.Id))
                throw new InvalidOperationException("Observation rows must depend only on the single implementation unit and have no consumers.");
        }
        if (edges.Any(e => !targetIds.Contains(e.Blocker) || !(targetIds.Contains(e.Blocked) || sourceIds.Contains(e.Blocked)) || e.Kind != "blocks"))
            throw new InvalidOperationException("External or ambiguous dependencies require explicit grooming revision.");
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        bool Cyclic(string id)
        {
            if (visited.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            if (edges.Where(e => e.Blocker == id && targetIds.Contains(e.Blocked)).Any(e => Cyclic(e.Blocked))) return true;
            visiting.Remove(id); visited.Add(id); return false;
        }
        if (targetIds.Any(Cyclic)) throw new InvalidOperationException("Cyclic implementation prerequisites prevent repair.");
        var transferred = sources.SelectMany(x => bindings[x.Id].Criteria).GroupBy(x => x.Id, StringComparer.Ordinal).Select(group =>
        {
            if (group.Select(SprintPlanJson.Serialize).Distinct().Count() != 1) throw new InvalidOperationException("Conflicting acceptance criterion IDs.");
            return group.First();
        }).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (transferred.Any(x => first.Criteria.Any(y => y.Id == x.Id))) throw new InvalidOperationException("Transferred criterion IDs collide with existing implementation criteria.");
        var merged = first.Criteria.Concat(transferred).ToArray();
        var replacements = targets.ToDictionary(x => x.Id, x => bindings[x.Id] with
        {
            Criteria = merged,
            CriterionIds = bindings[x.Id].CriterionIds.Concat(transferred.Select(y => y.Id)).Distinct().ToArray()
        });
        foreach (var (id, binding) in replacements) SprintOutcomeAuthority.ValidateShape(id, binding);
        var hash = SprintPlanJson.Hash(new { project = issues.ProjectId, specId, version, specUpdated, versionCreated, runId, runStarted, rows, edges, replacements });
        return new(issues.ProjectId, specId, version, runId, hash, authority, first.OutcomeId, first.Objective, sources, targets, transferred, replacements);
    }
}
