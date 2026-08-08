using System.Data.Common;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;
using Forge.Specs;

namespace Forge.Core;

public enum SpecStatus
{
    Draft,
    ReadyForDesign,
    Designed,
    AssetReady,
    NeedsRevision,
    Approved,
    Grooming,
    Groomed,
    Shipped,
    Superseded,
    Archived,
}

/// <summary>
/// Validates <see cref="SpecStatus"/> transitions. Enforced by
/// <see cref="ISpecStore.SetStatusAsync"/>; rejected transitions
/// throw <see cref="InvalidOperationException"/> so the call site
/// fails fast. New here for P2.a: the Designer step adds
/// ReadyForDesign / Designed / NeedsRevision and a terminal
/// Groomed state so the Groomer gate has a single, clear predicate.
/// </summary>
public static class SpecStatusTransitions
{
    private static readonly Dictionary<SpecStatus, HashSet<SpecStatus>> Allowed = new()
    {
        [SpecStatus.Draft] = new()
        {
            SpecStatus.ReadyForDesign,
            SpecStatus.Approved,
            SpecStatus.NeedsRevision,
            SpecStatus.Superseded,
            SpecStatus.Archived,
        },
        [SpecStatus.ReadyForDesign] = new()
        {
            SpecStatus.Designed,
            SpecStatus.NeedsRevision,
            SpecStatus.Approved,
            SpecStatus.Draft,
            SpecStatus.Superseded,
            SpecStatus.Archived,
        },
        [SpecStatus.Designed] = new()
        {
            SpecStatus.AssetReady,
            SpecStatus.Grooming,
            SpecStatus.NeedsRevision,
            SpecStatus.Draft,
            SpecStatus.Superseded,
            SpecStatus.Archived,
        },
        [SpecStatus.AssetReady] = new()
        {
            SpecStatus.Grooming,
            SpecStatus.NeedsRevision,
            SpecStatus.Draft,
            SpecStatus.Superseded,
            SpecStatus.Archived,
        },
        [SpecStatus.NeedsRevision] = new()
        {
            SpecStatus.Draft,
            SpecStatus.ReadyForDesign,
            SpecStatus.Superseded,
            SpecStatus.Archived,
        },
        [SpecStatus.Approved] = new()
        {
            SpecStatus.Grooming,
            SpecStatus.Draft,
            SpecStatus.Superseded,
            SpecStatus.Archived,
        },
        [SpecStatus.Grooming] = new()
        {
            SpecStatus.Groomed,
            SpecStatus.Draft,
            SpecStatus.Superseded,
            SpecStatus.Archived,
        },
        [SpecStatus.Groomed] = new()
        {
            SpecStatus.Shipped,
            SpecStatus.Approved,        // operator: "re-decompose"
            SpecStatus.Draft,
            SpecStatus.Superseded,
            SpecStatus.Archived,
        },
        [SpecStatus.Shipped] = new()
        {
            SpecStatus.Archived,
        },
        [SpecStatus.Superseded] = new()
        {
            SpecStatus.Archived,
        },
        [SpecStatus.Archived] = new(),
    };

    public static bool IsAllowed(SpecStatus from, SpecStatus to)
    {
        if (from == to) return true; // idempotent
        return Allowed.TryGetValue(from, out var set) && set.Contains(to);
    }

    public static void EnsureAllowed(SpecStatus from, SpecStatus to)
    {
        if (!IsAllowed(from, to))
        {
            throw new InvalidOperationException(
                $"Invalid spec status transition: {from} -> {to}. " +
                $"Allowed from {from}: {string.Join(", ", Allowed.GetValueOrDefault(from) ?? new HashSet<SpecStatus>())}");
        }
    }
}

/// <summary>
/// A spec is a living document owned by the Product agent. Specs are
/// versioned: every <c>UpdateAsync</c> on the body appends a new
/// <see cref="SpecVersionRecord"/> and bumps
/// <see cref="SpecRecord.CurrentVersion"/>. Status changes
/// (Draft -> Approved, etc.) do not create a new version.
///
/// <para>
/// A spec optionally links to a parent issue (an epic from intake) via
/// <see cref="SpecRecord.ParentIssueId"/>, and to a parent spec via
/// <see cref="SpecRecord.ParentSpecId"/> (for child sub-specs).
/// </para>
/// </summary>
public sealed record SpecRecord(
    string Id,
    string ProjectId,
    string Title,
    SpecStatus Status,
    string? ParentIssueId,
    string? ParentSpecId,
    int CurrentVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string Body,
    string? Author);

public sealed record SpecVersionRecord(
    string SpecId,
    int Version,
    string Body,
    string? Author,
    DateTime CreatedAt);

public sealed record NewSpec(
    string ProjectId,
    string Title,
    string Body,
    string? Author = null,
    string? ParentIssueId = null,
    string? ParentSpecId = null);

public sealed record UpdateSpecBody(
    string Body,
    string? Author = null);

public interface ISpecStore
{
    Task<SpecRecord> CreateAsync(NewSpec spec, CancellationToken ct = default);
    Task<SpecRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<SpecRecord>> ListAsync(string? projectId, SpecStatus? status, CancellationToken ct = default);
    /// <summary>Append a new body version; bumps <c>CurrentVersion</c> + updated_at.</summary>
    Task<SpecRecord> UpdateBodyAsync(string id, UpdateSpecBody update, CancellationToken ct = default);
    /// <summary>Move a spec to a new status. Does NOT create a new version.</summary>
    Task<SpecRecord> SetStatusAsync(string id, SpecStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<SpecVersionRecord>> ListVersionsAsync(string id, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public sealed class SpecStore : ISpecStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    private readonly SpecBodyExtractor _extractor;
    private readonly DesignArtifactStore? _designArtifacts;
    public SpecStore(
        IssueStore issues,
        SpecBodyExtractor? extractor = null,
        DesignArtifactStore? designArtifacts = null)
    {
        _issues = issues;
        _extractor = extractor ?? new SpecBodyExtractor();
        _designArtifacts = designArtifacts;
    }

    private string T(string name) => _issues.Db.Dialect.Table(name);

    public async Task<SpecRecord> CreateAsync(NewSpec spec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spec.ProjectId))
            throw new ArgumentException("projectId is required", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Title))
            throw new ArgumentException("title is required", nameof(spec));

        var now = DateTime.UtcNow;
        var id = $"spec-{Guid.NewGuid():N}";

        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {T("spec")}
                (id, project_id, title, status, parent_issue_id, parent_spec_id, current_version, created_at, updated_at)
                VALUES (@id, @proj, @title, @status, @pIssue, @pSpec, 1, @now, @now)
                """;
            cmd.AddParam("@id", id);
            cmd.AddParam("@proj", spec.ProjectId);
            cmd.AddParam("@title", spec.Title);
            cmd.AddParam("@status", SpecStatus.Draft.ToString());
            cmd.AddParam("@pIssue", (object?)spec.ParentIssueId ?? DBNull.Value);
            cmd.AddParam("@pSpec", (object?)spec.ParentSpecId ?? DBNull.Value);
            cmd.AddParam("@now", IssueStore.DateFormatTime(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {T("spec_version")}
                (spec_id, version, body, author, created_at)
                VALUES (@sid, 1, @body, @author, @now)
                """;
            cmd.AddParam("@sid", id);
            cmd.AddParam("@body", spec.Body);
            cmd.AddParam("@author", (object?)spec.Author ?? DBNull.Value);
            cmd.AddParam("@now", IssueStore.DateFormatTime(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await PersistExtractionAsync(conn, tx, id, spec.Body, ct);

        await tx.CommitAsync(ct);
        // Round 'now' to the same precision IssueStore.DateFormat
        // (millisecond) so the in-memory UpdatedAt matches the
        // value the DB stored. Without this, the next update
        // would read back a truncated timestamp that could
        // compare < the in-memory copy, breaking the
        // "UpdatedAt >= created.UpdatedAt" assertion in
        // SpecStoreTests.UpdateBodyAsync_AppendsNewVersion_BumpsCurrent.
        var storedNow = IssueStore.ParseTime(IssueStore.DateFormatTime(now));
        return new SpecRecord(
            Id: id, ProjectId: spec.ProjectId, Title: spec.Title,
            Status: SpecStatus.Draft,
            ParentIssueId: spec.ParentIssueId, ParentSpecId: spec.ParentSpecId,
            CurrentVersion: 1, CreatedAt: storedNow, UpdatedAt: storedNow,
            Body: spec.Body, Author: spec.Author);
    }

    public async Task<SpecRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);

        SpecRecord? spec = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT s.id, s.project_id, s.title, s.status, s.parent_issue_id, s.parent_spec_id,
                s.current_version, s.created_at, s.updated_at,
                v.body, v.author
                FROM {T("spec")} s
                JOIN {T("spec_version")} v ON v.spec_id = s.id AND v.version = s.current_version
                WHERE s.id = @id
                """;
            cmd.AddParam("@id", id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            spec = new SpecRecord(
                Id: rd.GetString(0),
                ProjectId: rd.GetString(1),
                Title: rd.GetString(2),
                Status: Enum.Parse<SpecStatus>(rd.GetString(3)),
                ParentIssueId: rd.IsDBNull(4) ? null : rd.GetString(4),
                ParentSpecId: rd.IsDBNull(5) ? null : rd.GetString(5),
                CurrentVersion: rd.GetInt32(6),
                CreatedAt: IssueStore.ParseTime(rd.GetString(7)),
                UpdatedAt: IssueStore.ParseTime(rd.GetString(8)),
                Body: rd.GetString(9),
                Author: rd.IsDBNull(10) ? null : rd.GetString(10));
        }
        return spec;
    }

    public async Task<IReadOnlyList<SpecRecord>> ListAsync(
        string? projectId, SpecStatus? status, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);

        var list = new List<SpecRecord>();
        await using var cmd = conn.CreateCommand();
        var sql = $"""
            SELECT s.id, s.project_id, s.title, s.status, s.parent_issue_id, s.parent_spec_id,
            s.current_version, s.created_at, s.updated_at,
            v.body, v.author
            FROM {T("spec")} s
            JOIN {T("spec_version")} v ON v.spec_id = s.id AND v.version = s.current_version
            WHERE 1=1
            """;
        if (projectId is not null) sql += " AND s.project_id = @proj";
        if (status is not null) sql += " AND s.status = @status";
        sql += " ORDER BY s.updated_at DESC";
        cmd.CommandText = sql;
        if (projectId is not null) cmd.AddParam("@proj", projectId);
        if (status is not null) cmd.AddParam("@status", status.Value.ToString());

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new SpecRecord(
                Id: rd.GetString(0),
                ProjectId: rd.GetString(1),
                Title: rd.GetString(2),
                Status: Enum.Parse<SpecStatus>(rd.GetString(3)),
                ParentIssueId: rd.IsDBNull(4) ? null : rd.GetString(4),
                ParentSpecId: rd.IsDBNull(5) ? null : rd.GetString(5),
                CurrentVersion: rd.GetInt32(6),
                CreatedAt: IssueStore.ParseTime(rd.GetString(7)),
                UpdatedAt: IssueStore.ParseTime(rd.GetString(8)),
                Body: rd.GetString(9),
                Author: rd.IsDBNull(10) ? null : rd.GetString(10)));
        }
        return list;
    }

    public async Task<SpecRecord> UpdateBodyAsync(string id, UpdateSpecBody update, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id is required", nameof(id));
        if (string.IsNullOrWhiteSpace(update.Body))
            throw new ArgumentException("body is required", nameof(update));

        var now = DateTime.UtcNow;
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int nextVersion;
        await using (var cur = conn.CreateCommand())
        {
            cur.Transaction = tx;
            cur.CommandText = $"SELECT current_version FROM {T("spec")} WHERE id = @id";
            cur.AddParam("@id", id);
            var hit = await cur.ExecuteScalarAsync(ct);
            if (hit is null) throw new InvalidOperationException($"Spec {id} not found");
            nextVersion = Convert.ToInt32(hit) + 1;
        }

        // P5.3 — spec body split. The post-processor extracts
        // every `<!-- artifact:kind:title -->` block into a
        // separate design_artifact row, replacing the marker
        // with a `[read_artifact design-{id}]` placeholder. The
        // slim header becomes the spec's body; bodies are
        // fetched on demand by the next agent via the
        // read_artifact tool. Idempotent: re-running on a
        // post-processed body yields zero new artifacts
        // (the markers are gone after the first pass).
        var split = _extractor.ExtractForReadArtifact(id, nextVersion, update.Body);
        var storedBody = split.NewArtifacts.Count > 0 ? split.Header : update.Body;

        if (split.NewArtifacts.Count > 0 && _designArtifacts is not null)
        {
            foreach (var na in split.NewArtifacts)
            {
                // na.Id is the post-processor's deterministic id
                // (e.g. design-task-1-2-1); it matches the
                // placeholder text in split.Header exactly. The
                // DesignArtifactStore.CreateAsync is idempotent
                // for ids it sees again; if the operator runs the
                // post-processor twice, the second pass is a no-op
                // (the markers are gone after the first pass).
                await _designArtifacts.CreateAsync(new NewDesignArtifact(
                    SpecId: id,
                    Kind: ParseKind(na.Kind),
                    Title: na.Title,
                    Body: na.Body,
                    BodyKind: "markdown"));
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {T("spec_version")} (spec_id, version, body, author, created_at)
                VALUES (@sid, @v, @body, @author, @now)
                """;
            cmd.AddParam("@sid", id);
            cmd.AddParam("@v", nextVersion);
            cmd.AddParam("@body", storedBody);
            cmd.AddParam("@author", (object?)update.Author ?? DBNull.Value);
            cmd.AddParam("@now", IssueStore.DateFormatTime(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""UPDATE {T("spec")} SET current_version = @v, updated_at = @now WHERE id = @id""";
            cmd.AddParam("@v", nextVersion);
            cmd.AddParam("@now", IssueStore.DateFormatTime(now));
            cmd.AddParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await PersistExtractionAsync(conn, tx, id, update.Body, ct);

        await tx.CommitAsync(ct);
        var refreshed = await GetAsync(id, ct)
            ?? throw new InvalidOperationException($"Spec {id} not found after update");
        return refreshed;
    }

    public async Task<SpecRecord> SetStatusAsync(string id, SpecStatus status, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var current = await GetAsync(id, ct)
            ?? throw new InvalidOperationException($"Spec {id} not found");
        SpecStatusTransitions.EnsureAllowed(current.Status, status);
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""UPDATE {T("spec")} SET status = @status, updated_at = @now WHERE id = @id""";
        cmd.AddParam("@status", status.ToString());
        cmd.AddParam("@now", IssueStore.DateFormatTime(now));
        cmd.AddParam("@id", id);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0) throw new InvalidOperationException($"Spec {id} not found");
        // Publish AFTER the mutation commits; the publisher swallows
        // failures (a hint never breaks a DB mutation).
        if (current.Status != status)
        {
            var changedAt = new DateTimeOffset(now, TimeSpan.Zero);
            await _issues.Events.PublishAsync(new Messaging.SpecStatusChanged
            {
                MessageId = Messaging.SpecStatusChanged.IdFor(id, status.ToString(), changedAt),
                ProjectId = _issues.ProjectId,
                SpecId = id,
                FromStatus = current.Status.ToString(),
                ToStatus = status.ToString(),
                ChangedAt = changedAt,
            }, ct);
        }
        return (await GetAsync(id, ct))!;
    }

    public async Task<IReadOnlyList<SpecVersionRecord>> ListVersionsAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        var list = new List<SpecVersionRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT spec_id, version, body, author, created_at
            FROM {T("spec_version")} WHERE spec_id = @id ORDER BY version DESC
            """;
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new SpecVersionRecord(
                SpecId: rd.GetString(0),
                Version: rd.GetInt32(1),
                Body: rd.GetString(2),
                Author: rd.IsDBNull(3) ? null : rd.GetString(3),
                CreatedAt: IssueStore.ParseTime(rd.GetString(4))));
        }
        return list;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("spec")} WHERE id = @id";
        cmd.AddParam("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Re-extract the spec body and replace the derived tables
    /// (spec_diagram, spec_touches, spec_dep) atomically. Called
    /// from CreateAsync and UpdateBodyAsync inside the same
    /// transaction as the body write. spec_dep rows whose
    /// to_spec_id does not exist are skipped silently — the spec
    /// body may mention a future spec by name.
    /// </summary>
    private async Task PersistExtractionAsync(
        DbConnection conn,
        DbTransaction tx,
        string specId,
        string body,
        CancellationToken ct)
    {
        var extracted = _extractor.Extract(body);

        // Diagrams: full replace (delete + insert).
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {T("spec_diagram")} WHERE spec_id = @id";
            del.AddParam("@id", specId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var d in extracted.Diagrams)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = $"""
                INSERT INTO {T("spec_diagram")}
                (spec_id, ordinal, kind, source, title)
                VALUES (@sid, @ord, @kind, @src, @title)
                """;
            ins.AddParam("@sid", specId);
            ins.AddParam("@ord", d.Ordinal);
            ins.AddParam("@kind", d.Kind);
            ins.AddParam("@src", d.Source);
            ins.AddParam("@title", (object?)d.Title ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }

        // Touches: full replace.
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {T("spec_touches")} WHERE spec_id = @id";
            del.AddParam("@id", specId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var t in extracted.Touches)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = $"""
                INSERT INTO {T("spec_touches")}
                (spec_id, module_id, source, rationale, created_at)
                VALUES (@sid, @mod, 'auto', @rat, @now)
                """;
            ins.AddParam("@sid", specId);
            ins.AddParam("@mod", t.ModuleId);
            ins.AddParam("@rat", (object?)t.Rationale ?? DBNull.Value);
            ins.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
            await ins.ExecuteNonQueryAsync(ct);
        }

        // Deps: full replace. spec_dep is bidirectional in the data
        // model but the agent only declares from->to; we insert
        // exactly the declared rows. spec_diagram rows whose
        // to_spec_id does not exist are silently skipped.
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {T("spec_dep")} WHERE from_spec_id = @id";
            del.AddParam("@id", specId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var d in extracted.Deps)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = $"""
                INSERT INTO {T("spec_dep")}
                (from_spec_id, to_spec_id, kind, rationale, source, created_at)
                VALUES (@from, @to, @kind, @rat, 'auto', @now)
                """;
            ins.AddParam("@from", specId);
            ins.AddParam("@to", d.TargetSpecId);
            ins.AddParam("@kind", d.Kind);
            ins.AddParam("@rat", (object?)d.Rationale ?? DBNull.Value);
            ins.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
            await ins.ExecuteNonQueryAsync(ct);
        }

        // Bump extracted_at on spec.
        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = $"""UPDATE {T("spec")} SET extracted_at = @now WHERE id = @id""";
            upd.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
            upd.AddParam("@id", specId);
            await upd.ExecuteNonQueryAsync(ct);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }

    // P5.3 — converts the post-processor's lowercase kind string
    // (the db-stored form of DesignArtifactKind) back to the
    // enum value. Mirrors the toDbValue mapping in
    // DesignArtifactKindExtensions.
    private static DesignArtifactKind ParseKind(string dbValue) => dbValue switch
    {
        "wireframe" => DesignArtifactKind.Wireframe,
        "mockup" => DesignArtifactKind.Mockup,
        "component-spec" => DesignArtifactKind.ComponentSpec,
        "visual-rule" => DesignArtifactKind.VisualRule,
        _ => DesignArtifactKind.ComponentSpec,
    };
}

/// <summary>
/// No-op spec store used when the dashboard is run without a real
/// SpecStore. All read methods return empty; write methods throw
/// <see cref="NotSupportedException"/>.
/// </summary>
public sealed class NullSpecStore : ISpecStore
{
    public Task<SpecRecord> CreateAsync(NewSpec spec, CancellationToken ct = default)
        => throw new NotSupportedException("Specs are not configured on this dashboard instance.");
    public Task<SpecRecord?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult<SpecRecord?>(null);
    public Task<IReadOnlyList<SpecRecord>> ListAsync(string? projectId, SpecStatus? status, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SpecRecord>>(Array.Empty<SpecRecord>());
    public Task<SpecRecord> UpdateBodyAsync(string id, UpdateSpecBody update, CancellationToken ct = default)
        => throw new NotSupportedException("Specs are not configured on this dashboard instance.");
    public Task<SpecRecord> SetStatusAsync(string id, SpecStatus status, CancellationToken ct = default)
        => throw new NotSupportedException("Specs are not configured on this dashboard instance.");
    public Task<IReadOnlyList<SpecVersionRecord>> ListVersionsAsync(string id, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SpecVersionRecord>>(Array.Empty<SpecVersionRecord>());
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => throw new NotSupportedException("Specs are not configured on this dashboard instance.");
}
