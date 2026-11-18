namespace Forge.Core;

public static class ArtistStoreRouting
{
    public static async Task<IReadOnlyList<ArtOutput>> ListAsync(ISpecStore specs,
        string id, bool byProject, CancellationToken ct = default)
    {
        if (await DesignerStoreRouting.ResolveArtifactScopeAsync(specs, id, byProject, ct) is not { } scope) return [];
        var (owner, ids) = scope;
        var local = await new ArtOutputStore(owner.Database).ListBySpecsAsync(ids, ct);
        var history = await new OwnedArtifactHistoryStore(owner.Database).ListArtAsync(ids, ct);
        return ArtifactHistory.IncludeUnambiguousHistory(local, history, row => row.Id)
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }
}
