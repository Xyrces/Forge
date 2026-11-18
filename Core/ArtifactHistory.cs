using Forge.Core.Db;

namespace Forge.Core;

public sealed record ArtifactStoreSource(string ProjectId, IDbConnectionFactory Database, string? ArtOutputRoot)
{
    internal async Task<bool> HasForeignArtifactsAsync(CancellationToken ct)
    {
        var dialect = Database.Dialect;
        var specs = dialect.Table("spec");
        await using var connection = await Database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM {dialect.Table("design_artifact")} a
                WHERE NOT EXISTS (SELECT 1 FROM {specs} s WHERE s.id=a.spec_id)
            ) OR EXISTS (
                SELECT 1 FROM {dialect.Table("art_output")} a
                WHERE NOT EXISTS (SELECT 1 FROM {specs} s WHERE s.id=a.spec_id)
            ) THEN 1 ELSE 0 END
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 1;
    }
}
public sealed class ArtifactHistoryAmbiguousException(string artifactId)
    : InvalidOperationException($"Artifact {artifactId} exists in multiple historical stores.");

/// <summary>Combine owned records with unambiguous retained historical records.</summary>
public static class ArtifactHistory
{
    public static IReadOnlyList<T> IncludeUnambiguousHistory<T>(IReadOnlyList<T> owned,
        IEnumerable<T> historical, Func<T, string> id)
    {
        var ownerIds = owned.Select(id).ToHashSet(StringComparer.Ordinal);
        return owned.Concat(historical.Where(row => !ownerIds.Contains(id(row)))
            .GroupBy(id, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single())).ToArray();
    }
}
