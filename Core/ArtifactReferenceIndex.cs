using Forge.Core.Db;

namespace Forge.Core;

/// <summary>Only the reference metadata needed in an engineering prompt. Bodies are fetched on demand.</summary>
internal sealed record ArtifactReference(string Id, string Kind, string BodyKind, string Title);

internal static class ArtifactReferenceIndex
{
    internal static async Task<IReadOnlyList<ArtifactReference>> ReadAsync(
        IDbConnectionFactory db, string table, string specId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, kind, body_kind, title FROM {db.Dialect.Table(table)} WHERE spec_id=@spec";
        command.AddParam("@spec", specId);
        var references = new List<ArtifactReference>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            references.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return references;
    }
}
