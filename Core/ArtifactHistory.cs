using Forge.Core.Db;

namespace Forge.Core;

public sealed record ArtifactStoreSource(string ProjectId, IDbConnectionFactory Database, string? ArtOutputRoot);
public sealed record LocatedArtifact<T>(T Artifact, ArtifactStoreSource Source);
public sealed class ArtifactHistoryAmbiguousException(string artifactId)
    : InvalidOperationException($"Artifact {artifactId} exists in multiple historical stores.");

/// <summary>Locate legacy records by their global ID and the reader's physical spec ownership.</summary>
public sealed class ArtifactHistory(ISpecStore owner,
    Func<CancellationToken, Task<IReadOnlyList<ArtifactStoreSource>>> sources)
{
    public static IReadOnlyList<T> IncludeUnambiguousHistory<T>(IReadOnlyList<T> owned,
        IEnumerable<T> historical, Func<T, string> id)
    {
        var ownerIds = owned.Select(id).ToHashSet(StringComparer.Ordinal);
        return owned.Concat(historical.Where(row => !ownerIds.Contains(id(row)))
            .GroupBy(id, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single())).ToArray();
    }

    public Task<LocatedArtifact<DesignArtifact>?> FindDesignAsync(string id, CancellationToken ct = default)
        => FindAsync(id, source => new DesignArtifactStore(source.Database).GetSpecIdAsync(id, ct),
            source => new DesignArtifactStore(source.Database).GetAsync(id, ct), ct);

    public Task<LocatedArtifact<ArtOutput>?> FindArtAsync(string id, CancellationToken ct = default)
        => FindAsync(id, source => new ArtOutputStore(source.Database).GetSpecIdAsync(id, ct),
            source => new ArtOutputStore(source.Database).GetAsync(id, ct), ct);

    private async Task<LocatedArtifact<T>?> FindAsync<T>(string id,
        Func<ArtifactStoreSource, Task<string?>> specId,
        Func<ArtifactStoreSource, Task<T?>> read, CancellationToken ct) where T : class
    {
        if (owner is ProjectRoutingSpecStore)
            throw new ArgumentException("Artifact history requires the reader's physical spec store.", nameof(owner));
        ArtifactStoreSource? found = null;
        foreach (var source in (await sources(ct)).DistinctBy(source =>
            (source.Database.Provider, source.Database.ConnectionString, source.Database.Qualifier)))
        {
            var owningSpec = await specId(source);
            if (owningSpec is null || await owner.GetAsync(owningSpec, ct) is null) continue;
            if (found is not null) throw new ArtifactHistoryAmbiguousException(id);
            found = source;
        }
        if (found is null) return null;
        var artifact = await read(found);
        return artifact is null ? null : new(artifact, found);
    }
}
