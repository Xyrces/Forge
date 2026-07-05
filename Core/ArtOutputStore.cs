using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Forge.Core;

/// <summary>
/// CRUD for <see cref="ArtOutput"/> rows. Lives in the same
/// DB as <see cref="IssueStore"/>; the schema is part of v10.
///
/// <para>
/// No SQL foreign key on <c>spec_id</c>: the <c>spec</c> table is
/// managed by <see cref="SpecStore"/> and has its own create
/// path. The dashboard + Artist agent are the source of truth
/// for the relationship.
/// </para>
/// </summary>
public sealed class ArtOutputStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public ArtOutputStore(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    public string DbPath => _dbPath;

    public async Task<ArtOutput> CreateAsync(NewArtOutput req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.SpecId))
            throw new ArgumentException("specId required", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ArgumentException("title required", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Body))
            throw new ArgumentException("body required", nameof(req));

        var id = $"art-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO art_output(
                id, spec_id, kind, title, body, body_kind,
                references_json, parent_artifact_id, status, author, created_at, updated_at)
            VALUES(
                $id, $spec, $kind, $title, $body, $body_kind,
                $refs, $parent, $status, $author, $ts, $ts)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$spec", req.SpecId);
        cmd.Parameters.AddWithValue("$kind", req.Kind.ToDbValue());
        cmd.Parameters.AddWithValue("$title", req.Title);
        cmd.Parameters.AddWithValue("$body", req.Body);
        cmd.Parameters.AddWithValue("$body_kind", req.BodyKind);
        cmd.Parameters.AddWithValue("$refs", (object?)req.ReferencesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$parent", (object?)req.ParentArtifactId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", req.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$author", req.Author);
        cmd.Parameters.AddWithValue("$ts", now.ToString(IssueStore.DateFormat));
        await cmd.ExecuteNonQueryAsync(ct);
        return new ArtOutput(
            Id: id, SpecId: req.SpecId, Kind: req.Kind, Title: req.Title,
            Body: req.Body, BodyKind: req.BodyKind, ReferencesJson: req.ReferencesJson,
            ParentArtifactId: req.ParentArtifactId, Status: req.Status,
            Author: req.Author, CreatedAt: now, UpdatedAt: now);
    }

    public async Task<ArtOutput?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, spec_id, kind, title, body, body_kind,
                   references_json, parent_artifact_id, status, author, created_at, updated_at
            FROM art_output WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? ReadRow(rd) : null;
    }

    public async Task<IReadOnlyList<ArtOutput>> ListBySpecAsync(
        string specId, ArtOutputStatus? status = null, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (status is null)
        {
            cmd.CommandText = """
                SELECT id, spec_id, kind, title, body, body_kind,
                       references_json, parent_artifact_id, status, author, created_at, updated_at
                FROM art_output
                WHERE spec_id = $spec
                ORDER BY created_at ASC
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT id, spec_id, kind, title, body, body_kind,
                       references_json, parent_artifact_id, status, author, created_at, updated_at
                FROM art_output
                WHERE spec_id = $spec AND status = $status
                ORDER BY created_at ASC
                """;
            cmd.Parameters.AddWithValue("$status", status.Value.ToString().ToLowerInvariant());
        }
        cmd.Parameters.AddWithValue("$spec", specId);
        var list = new List<ArtOutput>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(ReadRow(rd));
        return list;
    }

    public async Task<int> DeleteBySpecAsync(string specId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM art_output WHERE spec_id = $spec";
        cmd.Parameters.AddWithValue("$spec", specId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ArtOutput ReadRow(SqliteDataReader rd)
    {
        var kindStr = rd.GetString(2);
        ArtOutputKindExtensions.TryParseDb(kindStr, out var kind);
        var statusStr = rd.GetString(8);
        var status = statusStr switch
        {
            "draft" => ArtOutputStatus.Draft,
            "approved" => ArtOutputStatus.Approved,
            "superseded" => ArtOutputStatus.Superseded,
            _ => ArtOutputStatus.Draft,
        };
        return new ArtOutput(
            Id: rd.GetString(0),
            SpecId: rd.GetString(1),
            Kind: kind,
            Title: rd.GetString(3),
            Body: rd.GetString(4),
            BodyKind: rd.GetString(5),
            ReferencesJson: rd.IsDBNull(6) ? null : rd.GetString(6),
            ParentArtifactId: rd.IsDBNull(7) ? null : rd.GetString(7),
            Status: status,
            Author: rd.GetString(9),
            CreatedAt: DateTime.ParseExact(rd.GetString(10), IssueStore.DateFormat, CultureInfo.InvariantCulture),
            UpdatedAt: DateTime.ParseExact(rd.GetString(11), IssueStore.DateFormat, CultureInfo.InvariantCulture));
    }
}

public sealed record NewArtOutput(
    string SpecId,
    ArtOutputKind Kind,
    string Title,
    string Body,
    string BodyKind,
    string? ReferencesJson = null,
    string? ParentArtifactId = null,
    ArtOutputStatus Status = ArtOutputStatus.Draft,
    string Author = "artist");
