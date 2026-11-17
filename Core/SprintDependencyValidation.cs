using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class SprintStore
{
    private async Task ValidateExistingMembersAsync(DbConnection conn, DbTransaction tx,
        SprintActivationSnapshot snapshot, string? sprintId, CancellationToken ct)
    {
        if (snapshot.ExistingSprintMembers is null) return;
        if (sprintId is null)
            throw new InvalidOperationException("Existing membership is valid only for an active sprint amendment.");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT i.id,i.type FROM {T("sprint_issue")} m LEFT JOIN {T("issue")} i ON i.id=m.issue_id WHERE m.sprint_id=@sprint";
        cmd.AddParam("@sprint", sprintId);
        var current = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) throw new InvalidOperationException("Active sprint contains a missing issue.");
            var type = reader.GetString(1);
            if (!AgentTaskTypes.IsContainer(type) && type != AgentTaskTypes.PrWatch)
                current.Add(reader.GetString(0));
        }
        if (!current.SetEquals(snapshot.ExistingSprintMembers.Select(i => i.Id)))
            throw new InvalidOperationException("Active sprint membership changed during amendment.");
    }

    private async Task<List<SprintDependencySnapshot>> ReadDependenciesAsync(DbConnection conn, DbTransaction tx,
        IEnumerable<string> ids, CancellationToken ct)
    {
        var result = new List<SprintDependencySnapshot>();
        foreach (var batch in ids.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var parameters = batch.Select((_, index) => "@id" + index).ToArray();
            cmd.CommandText = $"SELECT d.blocker_id,d.blocked_id,b.status FROM {T("issue_dep")} d LEFT JOIN {T("issue")} b ON b.id=d.blocker_id WHERE d.kind='blocks' AND d.blocked_id IN ({string.Join(",", parameters)})";
            for (var i = 0; i < batch.Length; i++) cmd.AddParam(parameters[i], batch[i]);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : Enum.Parse<IssueStatus>(reader.GetString(2))));
        }
        return result;
    }

    private async Task ValidateDependenciesAsync(DbConnection conn, DbTransaction tx,
        SprintActivationSnapshot snapshot, CancellationToken ct)
    {
        var membership = snapshot.TaskIds.Concat(snapshot.ExistingSprintMembers?.Select(i => i.Id) ?? []).ToHashSet(StringComparer.Ordinal);
        if (membership.Count > 1000)
            throw new InvalidOperationException("Dependency closure exceeds the 1000-node validation bound; decompose the work.");
        var actual = await ReadDependenciesAsync(conn, tx, membership, ct);
        if (snapshot.Dependencies is not null
            && (snapshot.Dependencies.Count != actual.Count || !actual.ToHashSet().SetEquals(snapshot.Dependencies)))
            throw new InvalidOperationException("Sprint dependency edges or prerequisite states changed during activation.");
        foreach (var edge in actual)
        {
            // Closed retains legacy dependency semantics here; it is not proof of delivery.
            if (edge.BlockerStatus is IssueStatus.Completed or IssueStatus.Closed) continue;
            if (edge.BlockerStatus is null or IssueStatus.Failed or IssueStatus.Blocked
                || snapshot.Dependencies is null || !membership.Contains(edge.BlockerId))
                throw new InvalidOperationException($"Sprint task {edge.BlockedId} has an unresolved prerequisite {edge.BlockerId}.");
        }
        var incoming = actual.Where(e => membership.Contains(e.BlockerId)
            && e.BlockerStatus is not (IssueStatus.Completed or IssueStatus.Closed))
            .GroupBy(e => e.BlockedId).ToDictionary(g => g.Key, g => g.Select(e => e.BlockerId).ToArray());
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("Sprint dependency closure contains a cycle.");
            if (incoming.TryGetValue(id, out var prerequisites)) foreach (var prerequisite in prerequisites) Visit(prerequisite);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var id in membership) Visit(id);
    }

    private async Task ValidateNecessityAsync(DbConnection conn, DbTransaction tx, string sprintId,
        SprintActivationSnapshot snapshot, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT i.id FROM {T("sprint_issue")} m JOIN {T("issue")} i ON i.id=m.issue_id WHERE m.sprint_id=@sprint AND i.status NOT IN ('Completed','Closed')";
        cmd.AddParam("@sprint", sprintId);
        var needed = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) needed.Add(reader.GetString(0));
        var edges = await ReadDependenciesAsync(conn, tx, needed.Concat(snapshot.TaskIds), ct);
        // Fixed point walks backwards from existing unfinished work through selected additions.
        bool changed;
        do
        {
            changed = false;
            foreach (var edge in edges)
                if (needed.Contains(edge.BlockedId) && snapshot.TaskIds.Contains(edge.BlockerId))
                    changed |= needed.Add(edge.BlockerId);
        } while (changed);
        if (snapshot.TaskIds.Any(id => !needed.Contains(id)))
            throw new InvalidOperationException("Amendment contains work that is not a prerequisite of the active sprint.");
    }
}
