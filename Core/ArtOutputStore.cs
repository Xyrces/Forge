using System.Globalization;
using System.Data.Common;
using Forge.Core.Db;
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
    private readonly IDbConnectionFactory _db;
    private readonly string _dbPath;

    public ArtOutputStore(string dbPath)
        : this(ForgeDb.Sqlite(BuildSqliteConnectionString(dbPath)))
    {
        _dbPath = dbPath;
    }

    public ArtOutputStore(IDbConnectionFactory db)
    {
        _db = db;
        _dbPath = "";
    }

    private static string BuildSqliteConnectionString(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    private string T(string name) => _db.Dialect.Table(name);

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
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {T("art_output")}(
                id, spec_id, kind, title, body, body_kind,
                references_json, parent_artifact_id, status, author, created_at, updated_at)
            VALUES(
                @id, @spec, @kind, @title, @body, @body_kind,
                @refs, @parent, @status, @author, @ts, @ts)
            """;
        cmd.AddParam("@id", id);
        cmd.AddParam("@spec", req.SpecId);
        cmd.AddParam("@kind", req.Kind.ToDbValue());
        cmd.AddParam("@title", req.Title);
        cmd.AddParam("@body", req.Body);
        cmd.AddParam("@body_kind", req.BodyKind);
        cmd.AddParam("@refs", (object?)req.ReferencesJson ?? DBNull.Value);
        cmd.AddParam("@parent", (object?)req.ParentArtifactId ?? DBNull.Value);
        cmd.AddParam("@status", req.Status.ToString().ToLowerInvariant());
        cmd.AddParam("@author", req.Author);
        cmd.AddParam("@ts", now.ToString(IssueStore.DateFormat));
        await cmd.ExecuteNonQueryAsync(ct);
        return new ArtOutput(
            Id: id, SpecId: req.SpecId, Kind: req.Kind, Title: req.Title,
            Body: req.Body, BodyKind: req.BodyKind, ReferencesJson: req.ReferencesJson,
            ParentArtifactId: req.ParentArtifactId, Status: req.Status,
            Author: req.Author, CreatedAt: now, UpdatedAt: now);
    }

    public async Task<ArtOutput?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, spec_id, kind, title, body, body_kind,
                   references_json, parent_artifact_id, status, author, created_at, updated_at
            FROM {T("art_output")} WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? ReadRow(rd) : null;
    }

    public async Task<IReadOnlyList<ArtOutput>> ListBySpecAsync(
        string specId, ArtOutputStatus? status = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (status is null)
        {
            cmd.CommandText = $"""
                SELECT id, spec_id, kind, title, body, body_kind,
                       references_json, parent_artifact_id, status, author, created_at, updated_at
                FROM {T("art_output")}
                WHERE spec_id = @spec
                ORDER BY created_at ASC
                """;
        }
        else
        {
            cmd.CommandText = $"""
                SELECT id, spec_id, kind, title, body, body_kind,
                       references_json, parent_artifact_id, status, author, created_at, updated_at
                FROM {T("art_output")}
                WHERE spec_id = @spec AND status = @status
                ORDER BY created_at ASC
                """;
            cmd.AddParam("@status", status.Value.ToString().ToLowerInvariant());
        }
        cmd.AddParam("@spec", specId);
        var list = new List<ArtOutput>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(ReadRow(rd));
        return list;
    }

    public async Task<IReadOnlyList<ArtOutput>> ListByProjectAsync(
        string projectId, ArtOutputStatus? status = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (status is null)
        {
            cmd.CommandText = $"""
                SELECT ao.id, ao.spec_id, ao.kind, ao.title, ao.body, ao.body_kind,
                       ao.references_json, ao.parent_artifact_id, ao.status, ao.author,
                       ao.created_at, ao.updated_at
                FROM {T("art_output")} ao
                JOIN {T("spec")} s ON s.id = ao.spec_id
                WHERE s.project_id = @project
                ORDER BY ao.created_at ASC
                """;
        }
        else
        {
            cmd.CommandText = $"""
                SELECT ao.id, ao.spec_id, ao.kind, ao.title, ao.body, ao.body_kind,
                       ao.references_json, ao.parent_artifact_id, ao.status, ao.author,
                       ao.created_at, ao.updated_at
                FROM {T("art_output")} ao
                JOIN {T("spec")} s ON s.id = ao.spec_id
                WHERE s.project_id = @project AND ao.status = @status
                ORDER BY ao.created_at ASC
                """;
            cmd.AddParam("@status", status.Value.ToString().ToLowerInvariant());
        }
        cmd.AddParam("@project", projectId);
        var list = new List<ArtOutput>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(ReadRow(rd));
        return list;
    }

    public async Task<int> DeleteBySpecAsync(string specId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("art_output")} WHERE spec_id = @spec";
        cmd.AddParam("@spec", specId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ArtOutput ReadRow(DbDataReader rd)
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
