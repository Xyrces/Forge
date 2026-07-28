using System.Data.Common;
using System.Text;
using Forge.Core.Db;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Forge.Core;

/// <summary>
/// Secret kinds. Open enum (new strings are allowed without code
/// changes here; the SecretStore + endpoints just store ciphertext
/// + tag). Used by the LLM/GitHub/Meshy provider paths to fall
/// back from per-project DB secrets to the global env/appsettings
/// values when no row is set.
/// </summary>
public static class SecretKinds
{
    public const string GitHubToken = "github_token";
    public const string KiloGatewayApiKey = "kilo_gateway_api_key";
    public const string MeshyApiKey = "meshy_api_key";
    /// <summary>Kimi.com (Moonshot) direct API key — the kimi LLM
    /// provider for quality roles (reviewer/critic/groomer).</summary>
    public const string KimiApiKey = "kimi_api_key";
}

public sealed record SecretRecord(
    string Id,
    string ProjectId,
    string Kind,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>
    /// Opaque ciphertext. The IDataProtector that wrote it is
    /// the only thing that can decrypt it. Callers receive
    /// <see cref="Plaintext"/> via <see cref="ISecretStore.GetAsync"/>
    /// when they have authorization (currently: any caller; auth
    /// is the operator — there's no per-user auth model yet).
    /// </summary>
    public byte[] Ciphertext { get; init; } = Array.Empty<byte>();
}

public sealed record NewSecret(
    string ProjectId,
    string Kind,
    byte[] Plaintext);

public interface ISecretStore
{
    Task<SecretRecord?> GetMetadataAsync(string projectId, string kind, CancellationToken ct = default);
    Task<string?> GetPlaintextAsync(string projectId, string kind, CancellationToken ct = default);
    Task<IReadOnlyList<SecretRecord>> ListAsync(string projectId, CancellationToken ct = default);
    Task<SecretRecord> UpsertAsync(NewSecret secret, CancellationToken ct = default);
    Task<bool> DeleteAsync(string projectId, string kind, CancellationToken ct = default);
}

/// <summary>
/// SQLite-backed secret store. Ciphertext is encrypted with
/// <see cref="IDataProtector"/> using a "forge.secret.v1" purpose
/// string. The DataProtection master key comes from the standard
/// location (<c>~/.aspnet/DataProtection-Keys/</c> on Linux) — the
/// system installer writes that keyring on first run; rotating the
/// key invalidates all secrets (operators must re-enter).
///
/// <para>
/// Multi-tenant-ready: every secret is scoped to a project_id.
/// For the v1 "single user, multiple projects" model the project
/// IS the tenant; the same table will hold secrets for unrelated
/// customers once the dashboard adds per-user auth.
/// </para>
/// </summary>
public sealed class SecretStore : ISecretStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    private readonly IDataProtector _protector;
    private readonly ILogger<SecretStore>? _logger;

    private string T(string name) => _issues.Db.Dialect.Table(name);
    private ISqlDialect D => _issues.Db.Dialect;

    public SecretStore(IssueStore issues, IDataProtectionProvider provider, ILogger<SecretStore>? logger = null)
    {
        _issues = issues;
        // Purpose string narrows the scope of the protected
        // payload. If the DataProtection keyring is rotated,
        // every secret becomes unreadable — operators re-enter
        // them via the dashboard.
        _protector = provider.CreateProtector("forge.secret.v1");
        _logger = logger;
    }

    public async Task<SecretRecord?> GetMetadataAsync(string projectId, string kind, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {D.Top(1)}id, project_id, kind, created_at, updated_at FROM {T("secret")} WHERE project_id = @p AND kind = @k{D.Limit(1)}";
        cmd.AddParam("@p", projectId);
        cmd.AddParam("@k", kind);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? new SecretRecord(
            Id: rd.GetString(0),
            ProjectId: rd.GetString(1),
            Kind: rd.GetString(2),
            CreatedAt: IssueStore.ParseTime(rd.GetString(3)),
            UpdatedAt: IssueStore.ParseTime(rd.GetString(4))) : null;
    }

    public async Task<string?> GetPlaintextAsync(string projectId, string kind, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {D.Top(1)}ciphertext FROM {T("secret")} WHERE project_id = @p AND kind = @k{D.Limit(1)}";
        cmd.AddParam("@p", projectId);
        cmd.AddParam("@k", kind);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return null;
        try
        {
            var ciphertext = (byte[])result;
            var plaintext = _protector.Unprotect(ciphertext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            // If the master key was rotated, all stored secrets
            // are unreadable. We log + return null; callers fall
            // back to the env/appsettings value.
            _logger?.LogWarning(ex, "Failed to decrypt secret '{Kind}' for project '{ProjectId}'; likely master-key rotation", kind, projectId);
            return null;
        }
    }

    public async Task<IReadOnlyList<SecretRecord>> ListAsync(string projectId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, project_id, kind, created_at, updated_at FROM {T("secret")} WHERE project_id = @p ORDER BY kind";
        cmd.AddParam("@p", projectId);
        var list = new List<SecretRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new SecretRecord(
                Id: rd.GetString(0),
                ProjectId: rd.GetString(1),
                Kind: rd.GetString(2),
                CreatedAt: IssueStore.ParseTime(rd.GetString(3)),
                UpdatedAt: IssueStore.ParseTime(rd.GetString(4))));
        }
        return list;
    }

    public async Task<SecretRecord> UpsertAsync(NewSecret secret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secret.ProjectId))
            throw new InvalidOperationException("Secret.ProjectId is required.");
        if (string.IsNullOrWhiteSpace(secret.Kind))
            throw new InvalidOperationException("Secret.Kind is required.");
        if (secret.Plaintext is null || secret.Plaintext.Length == 0)
            throw new InvalidOperationException("Secret.Plaintext is empty.");

        var ciphertext = _protector.Protect(secret.Plaintext);
        var now = DateTime.UtcNow;

        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Insert-or-replace keyed on (project_id, kind). The id
        // is a fresh UUID per write so audit logging can see
        // "secret X rotated" as a new event.
        await using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = _issues.Db.Provider == ForgeDbProvider.SqlServer
                ? "MERGE " + T("secret") + @" WITH (HOLDLOCK) AS t
                    USING (SELECT @p AS project_id, @k AS kind) AS s
                        ON t.project_id = s.project_id AND t.kind = s.kind
                    WHEN MATCHED THEN UPDATE SET ciphertext = @c, updated_at = @now
                    WHEN NOT MATCHED THEN INSERT (id, project_id, kind, ciphertext, created_at, updated_at)
                        VALUES (@id, @p, @k, @c, @now, @now);"
                : @"INSERT INTO secret (id, project_id, kind, ciphertext, created_at, updated_at)
                    VALUES (@id, @p, @k, @c, @now, @now)
                    ON CONFLICT(project_id, kind) DO UPDATE SET
                        ciphertext  = excluded.ciphertext,
                        updated_at  = excluded.updated_at";
            upsert.AddParam("@id", $"secret-{Guid.NewGuid():N}");
            upsert.AddParam("@p", secret.ProjectId);
            upsert.AddParam("@k", secret.Kind);
            upsert.AddParam("@c", ciphertext);
            upsert.AddParam("@now", IssueStore.DateFormatTime(now));
            await upsert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        _logger?.LogInformation("Stored encrypted secret '{Kind}' for project '{ProjectId}'", secret.Kind, secret.ProjectId);
        return (await GetMetadataAsync(secret.ProjectId, secret.Kind, ct))!;
    }

    public async Task<bool> DeleteAsync(string projectId, string kind, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("secret")} WHERE project_id = @p AND kind = @k";
        cmd.AddParam("@p", projectId);
        cmd.AddParam("@k", kind);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}
