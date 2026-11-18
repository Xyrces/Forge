using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

public sealed record OwnedHistoricalArtifact<T>(T Artifact, string WriterProjectId, string? ArtOutputRoot);

/// <summary>
/// Materialized legacy artifacts in their specification owner's database. Import is
/// controller work. Every ordinary read touches only the owner's physical store.
/// </summary>
public sealed class OwnedArtifactHistoryStore(IDbConnectionFactory ownerDatabase)
{
    private string Table => ownerDatabase.Dialect.Table("owned_artifact_history");
    private string Specs => ownerDatabase.Dialect.Table("spec");
    private string Unambiguous => $"""
        NOT EXISTS (SELECT 1 FROM {Table} other JOIN {Specs} owned ON owned.id=other.spec_id
            WHERE other.artifact_id=h.artifact_id AND other.artifact_type=h.artifact_type
              AND other.writer_project_id<>h.writer_project_id)
        """;

    public Task<OwnedHistoricalArtifact<DesignArtifact>?> GetDesignAsync(string id, CancellationToken ct = default)
        => GetAsync<DesignArtifact>(id, "design", ct);

    public Task<OwnedHistoricalArtifact<ArtOutput>?> GetArtAsync(string id, CancellationToken ct = default)
        => GetAsync<ArtOutput>(id, "art", ct);

    private async Task<OwnedHistoricalArtifact<T>?> GetAsync<T>(string id, string type, CancellationToken ct)
    {
        await using var connection = await ownerDatabase.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT h.record_json, h.writer_project_id, h.art_output_root
            FROM {Table} h JOIN {Specs} s ON s.id=h.spec_id
            WHERE h.artifact_id=@id AND h.artifact_type=@type
            """;
        command.AddParam("@id", id);
        command.AddParam("@type", type);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var record = JsonSerializer.Deserialize<T>(reader.GetString(0))
            ?? throw new InvalidDataException($"Historical artifact {id} has no record.");
        var result = new OwnedHistoricalArtifact<T>(record, reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
        if (await reader.ReadAsync(ct)) throw new ArtifactHistoryAmbiguousException(id);
        return result;
    }

    public async Task<IReadOnlyList<ArtifactReference>> ListReferencesAsync(string specId, CancellationToken ct = default)
    {
        await using var connection = await ownerDatabase.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // Body columns are deliberately absent from the dispatch projection.
        command.CommandText = $"""
            SELECT h.artifact_id, h.kind, h.body_kind, h.title
            FROM {Table} h JOIN {Specs} s ON s.id=h.spec_id
            WHERE h.spec_id=@spec AND {Unambiguous}
            """;
        command.AddParam("@spec", specId);
        var rows = new List<ArtifactReference>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return ArtifactHistory.IncludeUnambiguousHistory<ArtifactReference>([], rows, row => row.Id);
    }

    public async Task<IReadOnlyList<DesignArtifact>> ListDesignsAsync(
        IReadOnlyList<string> ownedSpecIds, CancellationToken ct = default)
    {
        var rows = await ListRecordsAsync<DesignArtifact>(ownedSpecIds, "design", ct);
        return ArtifactHistory.IncludeUnambiguousHistory<DesignArtifact>([], rows, row => row.Id)
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<ArtOutput>> ListArtAsync(
        IReadOnlyList<string> ownedSpecIds, CancellationToken ct = default)
    {
        var rows = await ListRecordsAsync<ArtOutput>(ownedSpecIds, "art", ct);
        return ArtifactHistory.IncludeUnambiguousHistory<ArtOutput>([], rows, row => row.Id)
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    private async Task<IReadOnlyList<T>> ListRecordsAsync<T>(
        IReadOnlyList<string> ownedSpecIds, string type, CancellationToken ct)
    {
        var rows = new List<T>();
        if (ownedSpecIds.Count == 0) return rows;
        await using var connection = await ownerDatabase.OpenAsync(ct);
        foreach (var batch in ownedSpecIds.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            await using var command = connection.CreateCommand();
            var parameters = batch.Select((id, index) => (Name: $"@spec{index}", Id: id)).ToArray();
            command.CommandText = $"""
                SELECT h.record_json FROM {Table} h JOIN {Specs} s ON s.id=h.spec_id
                WHERE h.artifact_type=@type AND h.spec_id IN ({string.Join(",", parameters.Select(p => p.Name))})
                    AND {Unambiguous}
                """;
            command.AddParam("@type", type);
            foreach (var parameter in parameters) command.AddParam(parameter.Name, parameter.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))
                    ?? throw new InvalidDataException("Historical artifact has no record."));
        }
        return rows;
    }

    /// <summary>
    /// Copy a writer's records for specs physically present in this owner. Successful
    /// batches persist across retries. Other writers' retained history is never removed.
    /// </summary>
    public async Task<int> ImportAsync(ArtifactStoreSource writer, SpecStore owner, CancellationToken ct = default)
    {
        if (!SameDatabase(owner.Database, ownerDatabase))
            throw new ArgumentException("Import requires the history store's physical spec owner.", nameof(owner));
        if (SameDatabase(writer.Database, ownerDatabase)) return 0;
        var count = 0;
        foreach (var batch in (await owner.ListOwnedIdsAsync(ct)).Chunk(100))
        {
            // Each writer query filters on its indexed spec_id column. It never
            // loads another project's bodies to decide which records to retain.
            var designs = await new DesignArtifactStore(writer.Database).ListBySpecsAsync(batch, ct);
            var art = await new ArtOutputStore(writer.Database).ListBySpecsAsync(batch, ct);
            await using var connection = await ownerDatabase.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            foreach (var row in designs)
                await UpsertAsync(connection, transaction, writer, row.Id, row.SpecId, "design",
                    row.Kind.ToDbValue(), row.BodyKind, row.Title, JsonSerializer.Serialize(row), ct);
            foreach (var row in art)
                await UpsertAsync(connection, transaction, writer, row.Id, row.SpecId, "art",
                    row.Kind.ToDbValue(), row.BodyKind, row.Title, JsonSerializer.Serialize(row), ct);
            await transaction.CommitAsync(ct);
            count += designs.Count + art.Count;
        }
        return count;
    }

    private async Task UpsertAsync(DbConnection connection, DbTransaction transaction,
        ArtifactStoreSource writer, string id, string specId, string type, string kind,
        string bodyKind, string title, string json, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ownerDatabase.Provider == ForgeDbProvider.SqlServer
            ? $"""
                MERGE {Table} WITH (HOLDLOCK) AS target
                USING (SELECT @writer AS writer_project_id, @id AS artifact_id) AS source
                ON target.writer_project_id=source.writer_project_id AND target.artifact_id=source.artifact_id
                WHEN MATCHED THEN UPDATE SET spec_id=@spec,artifact_type=@type,kind=@kind,
                    body_kind=@bodykind,title=@title,record_json=@json,art_output_root=@root
                WHEN NOT MATCHED THEN INSERT
                    (writer_project_id,artifact_id,spec_id,artifact_type,kind,body_kind,title,record_json,art_output_root)
                    VALUES (@writer,@id,@spec,@type,@kind,@bodykind,@title,@json,@root);
                """
            : $"""
                INSERT INTO {Table}
                    (writer_project_id,artifact_id,spec_id,artifact_type,kind,body_kind,title,record_json,art_output_root)
                VALUES (@writer,@id,@spec,@type,@kind,@bodykind,@title,@json,@root)
                ON CONFLICT(writer_project_id,artifact_id) DO UPDATE SET spec_id=excluded.spec_id,
                    artifact_type=excluded.artifact_type,kind=excluded.kind,body_kind=excluded.body_kind,
                    title=excluded.title,record_json=excluded.record_json,art_output_root=excluded.art_output_root;
                """;
        command.AddParam("@writer", writer.ProjectId);
        command.AddParam("@id", id);
        command.AddParam("@spec", specId);
        command.AddParam("@type", type);
        command.AddParam("@kind", kind);
        command.AddParam("@bodykind", bodyKind);
        command.AddParam("@title", title);
        command.AddParam("@json", json);
        command.AddParam("@root", (object?)writer.ArtOutputRoot ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool SameDatabase(IDbConnectionFactory left, IDbConnectionFactory right)
        => left.Provider == right.Provider && left.Qualifier == right.Qualifier
            && left.ConnectionString == right.ConnectionString;
}
