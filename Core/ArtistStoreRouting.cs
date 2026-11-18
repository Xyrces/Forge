namespace Forge.Core;

public static class ArtistStoreRouting
{
    public static async Task<IReadOnlyList<ArtOutput>> ListAsync(ISpecStore specs,
        string id, bool byProject, CancellationToken ct = default)
    {
        var owner = specs is ProjectRoutingSpecStore routing
            ? (byProject ? routing.FindRegisteredStore(id) : routing.ResolveOwner(id)) as SpecStore
            : specs as SpecStore;
        if (owner is null) return [];
        IReadOnlyList<string> ids = byProject ? await owner.ListOwnedIdsAsync(ct) : [id];
        if (!byProject && await owner.GetAsync(id, ct) is null) return [];
        var local = await new ArtOutputStore(owner.Database).ListBySpecsAsync(ids, ct);
        var history = await new OwnedArtifactHistoryStore(owner.Database).ListArtAsync(ids, ct);
        return ArtifactHistory.IncludeUnambiguousHistory(local, history, row => row.Id)
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }
}
