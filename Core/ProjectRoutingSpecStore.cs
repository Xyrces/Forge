using System.Collections.Concurrent;

namespace Forge.Core;

/// <summary>
/// Project-routing spec store (operator rule 2026-07-31: spec rows
/// are per-project workload data). The planning lane historically
/// wrote every spec to the PRIMARY store with only a project_id
/// column value — which the per-project lens then (correctly) hid,
/// stranding porthorizon's specs in the forge schema.
///
/// Routing rules:
/// <list type="bullet">
/// <item>Create: the store OWNING <c>NewSpec.ProjectId</c>.</item>
/// <item>Id-addressed ops (Get/UpdateBody/SetStatus/Versions/Delete):
/// spec ids are GUIDs with no ownership hint, so resolve the owner by
/// probing (primary first — legacy rows — then each project store)
/// and cache the result.</item>
/// <item>List with a project: that project's store (no column filter
/// — rows are homed). List WITHOUT a project: REJECTED (2026-08-09,
/// operator rule: schema-per-project IS the isolation boundary —
/// cross-project reads must be explicit). The only legitimate
/// cross-project readers are the pipeline schedulers that advance
/// every project's lane and the unified admin view; they call
/// <see cref="ListAcrossProjectsAsync"/> by name. A silent fan-out on
/// the shared interface let business logic match per-store-sequence
/// ids (epic-N/task-N) across projects (live incident 2026-08-09:
/// talaria's accepted epic-2 resolved to porthorizon's Epic-B spec —
/// no talaria spec was ever created, and the refinement queue tried
/// to transition another project's Groomed spec).</item>
/// </list>
/// </summary>
public sealed class ProjectRoutingSpecStore : ISpecStore
{
    private readonly ISpecStore _primary;
    private readonly Func<string, ISpecStore?> _findByProject;
    private readonly Func<IReadOnlyList<ISpecStore>> _allProjectStores;
    private readonly ConcurrentDictionary<string, ISpecStore> _ownerCache = new();

    /// <param name="primary">The primary project's store (fallback
    /// for unknown projects + legacy rows).</param>
    /// <param name="findByProject">Resolve a project's spec store;
    /// null when the project is unknown.</param>
    /// <param name="allProjectStores">Every registered project's
    /// spec store (for fan-out reads + owner probes). May include the
    /// primary — probes dedupe by reference.</param>
    public ProjectRoutingSpecStore(
        ISpecStore primary,
        Func<string, ISpecStore?> findByProject,
        Func<IReadOnlyList<ISpecStore>> allProjectStores)
    {
        _primary = primary;
        _findByProject = findByProject;
        _allProjectStores = allProjectStores;
    }

    private ISpecStore StoreFor(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return _primary;
        return _findByProject(projectId) ?? _primary;
    }

    private ISpecStore? ResolveOwner(string id)
    {
        if (_ownerCache.TryGetValue(id, out var cached)) return cached;
        var candidates = new List<ISpecStore> { _primary };
        candidates.AddRange(_allProjectStores().Where(s => !ReferenceEquals(s, _primary)));
        foreach (var store in candidates)
        {
            // GetAsync is the cheap existence probe (id-keyed).
            if (store.GetAsync(id).GetAwaiter().GetResult() is not null)
            {
                _ownerCache[id] = store;
                return store;
            }
        }
        return null;
    }

    public Task<SpecRecord> CreateAsync(NewSpec spec, CancellationToken ct = default) =>
        StoreFor(spec.ProjectId).CreateAsync(spec, ct);

    public async Task<SpecRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        var owner = ResolveOwner(id);
        return owner is null ? null : await owner.GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<SpecRecord>> ListAsync(string? projectId, SpecStatus? status, CancellationToken ct = default)
    {
        if (projectId is not null)
        {
            // Homed rows: no column filter (a stale project_id value
            // must not hide a row from its owning store's list).
            return await StoreFor(projectId).ListAsync(null, status, ct);
        }
        throw new InvalidOperationException(
            "Unscoped spec listing is not allowed on the routing store — schema-per-project " +
            "is the isolation boundary (operator rule 2026-08-09). Scope by project, or call " +
            "ListAcrossProjectsAsync explicitly (pipeline schedulers / unified admin view only).");
    }

    /// <summary>
    /// THE explicit cross-project read: fan out across the primary +
    /// every project store and merge. Restricted by convention to the
    /// pipeline schedulers that advance every project's lane
    /// (groomer/designer/artist sweeps) and the unified admin view.
    /// Callers must never match the merged rows by per-store-sequence
    /// ISSUE ids (epic-N, task-N — every project has them); spec ids
    /// are random hex and safe to key on.
    /// </summary>
    public async Task<IReadOnlyList<SpecRecord>> ListAcrossProjectsAsync(SpecStatus? status, CancellationToken ct = default)
    {
        var stores = new List<ISpecStore> { _primary };
        stores.AddRange(_allProjectStores().Where(s => !ReferenceEquals(s, _primary)));
        var merged = new List<SpecRecord>();
        foreach (var store in stores)
        {
            merged.AddRange(await store.ListAsync(null, status, ct));
        }
        return merged
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    public Task<SpecRecord> UpdateBodyAsync(string id, UpdateSpecBody update, CancellationToken ct = default) =>
        (ResolveOwner(id) ?? throw new InvalidOperationException($"spec {id} not found in any project store"))
            .UpdateBodyAsync(id, update, ct);

    public Task<SpecRecord> SetStatusAsync(string id, SpecStatus status, CancellationToken ct = default) =>
        (ResolveOwner(id) ?? throw new InvalidOperationException($"spec {id} not found in any project store"))
            .SetStatusAsync(id, status, ct);

    public async Task<IReadOnlyList<SpecVersionRecord>> ListVersionsAsync(string id, CancellationToken ct = default)
    {
        var owner = ResolveOwner(id);
        return owner is null
            ? Array.Empty<SpecVersionRecord>()
            : await owner.ListVersionsAsync(id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var owner = ResolveOwner(id);
        if (owner is null) return;
        await owner.DeleteAsync(id, ct);
        _ownerCache.TryRemove(id, out _);
    }
}

/// <summary>
/// The ONLY sanctioned cross-project spec read (operator rule
/// 2026-08-09: schema-per-project is the isolation boundary;
/// cross-project queries exist for exactly two situations — the
/// pipeline schedulers that advance every project's lane, and the
/// unified admin view). On the routing store this calls
/// <see cref="ProjectRoutingSpecStore.ListAcrossProjectsAsync"/> by
/// name; on a plain per-project store it is just the store's own
/// unscoped list. Never match the merged rows by per-store-sequence
/// issue ids (epic-N/task-N); spec ids are random hex and safe.
/// </summary>
public static class SpecStoreRoutingExtensions
{
    public static Task<IReadOnlyList<SpecRecord>> ListForPipelineSweepAsync(
        this ISpecStore specs, SpecStatus? status, CancellationToken ct)
        => specs is ProjectRoutingSpecStore routing
            ? routing.ListAcrossProjectsAsync(status, ct)
            : specs.ListAsync(null, status, ct);
}
