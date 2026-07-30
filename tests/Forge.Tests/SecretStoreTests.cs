using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Verifies the round-trip: encrypt + decrypt via IDataProtector,
/// CRUD against SQLite. Uses an in-memory ephemeral
/// DataProtectionProvider (no keyring on disk) so the test is
/// hermetic.
/// </summary>
public class SecretStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly ProjectStore _projects;
    private readonly SecretStore _store;

    public SecretStoreTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("secret");
        _issues = new IssueStore(_dbPath);
        _projects = new ProjectStore(_issues);
        // Ephemeral provider: the keys live only in process memory
        // and are discarded when the test ends. This is exactly
        // the in-memory provider Microsoft.AspNetCore.DataProtection
        // ships for unit tests; production uses the file-system
        // keyring at ~/.aspnet/DataProtection-Keys/.
        var provider = DataProtectionProvider.Create("forge.secrets.test");
        _store = new SecretStore(_issues, provider, NullLogger<SecretStore>.Instance);
    }

    /// <summary>
    /// The secret table has a foreign key on project(id). Tests
    /// that write secrets need to also seed the project row.
    /// </summary>
    private async Task SeedProjectAsync(string id)
    {
        await _projects.UpsertAsync(new NewProject(id, id, "https://example.com/" + id, "main"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Upsert_Get_RoundTripsPlaintext()
    {
        const string kind = "github_token";
        const string secret = "ghp_fake_doesnt_round_trip_xyz";
        var plaintext = Encoding.UTF8.GetBytes(secret);
        await SeedProjectAsync("forge");
        await _store.UpsertAsync(new NewSecret("forge", kind, plaintext), default);

        var got = await _store.GetPlaintextAsync("forge", kind, default);
        Assert.Equal(secret, got);
    }

    [Fact]
    public async Task Upsert_TwiceReplacesAndDoesNotDuplicate()
    {
        var kind = SecretKinds.KiloGatewayApiKey;
        await SeedProjectAsync("forge");
        await _store.UpsertAsync(new NewSecret("forge", kind, Encoding.UTF8.GetBytes("first")), default);
        await SeedProjectAsync("forge");
        await _store.UpsertAsync(new NewSecret("forge", kind, Encoding.UTF8.GetBytes("second")), default);

        var all = await _store.ListAsync("forge", default);
        Assert.Single(all);
        var got = await _store.GetPlaintextAsync("forge", kind, default);
        Assert.Equal("second", got);
    }

    [Fact]
    public async Task List_ReportsMetadataButNoCiphertext()
    {
        await SeedProjectAsync("forge");
        await _store.UpsertAsync(new NewSecret("forge", SecretKinds.GitHubToken, Encoding.UTF8.GetBytes("ghp_abc")), default);
        var list = await _store.ListAsync("forge", default);
        var row = Assert.Single(list);
        Assert.Equal(SecretKinds.GitHubToken, row.Kind);
        Assert.Equal("forge", row.ProjectId);
        Assert.Empty(row.Ciphertext); // list endpoint never exposes the ciphertext
        Assert.True(row.CreatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Get_UnsetKind_ReturnsNull()
    {
        var got = await _store.GetPlaintextAsync("forge", "unset_kind", default);
        Assert.Null(got);
        var metadata = await _store.GetMetadataAsync("forge", "unset_kind", default);
        Assert.Null(metadata);
    }

    [Fact]
    public async Task Delete_RemovesRowAndReturnsTrue()
    {
        var kind = SecretKinds.MeshyApiKey;
        await SeedProjectAsync("forge");
        await _store.UpsertAsync(new NewSecret("forge", kind, Encoding.UTF8.GetBytes("meshy_x")), default);
        Assert.True(await _store.DeleteAsync("forge", kind, default));
        Assert.Null(await _store.GetPlaintextAsync("forge", kind, default));
    }

    [Fact]
    public async Task Delete_Nonexistent_ReturnsFalse()
    {
        Assert.False(await _store.DeleteAsync("forge", "ghost", default));
    }

    [Fact]
    public async Task PerProjectIsolation_FetchingOneDoesNotLeakToOther()
    {
        // The project rows are pre-seeded via the project store (we
        // don't depend on ProjectStore here — just write secrets
        // directly; the FK in the schema is fine with dangling refs
        // because the unique constraint is on (project_id, kind)).
        await SeedProjectAsync("forge");
        await _store.UpsertAsync(new NewSecret("forge", SecretKinds.GitHubToken, Encoding.UTF8.GetBytes("forge_token")), default);
        await SeedProjectAsync("porthorizon");
        await _store.UpsertAsync(new NewSecret("porthorizon", SecretKinds.GitHubToken, Encoding.UTF8.GetBytes("ph_token")), default);

        Assert.Equal("forge_token", await _store.GetPlaintextAsync("forge", SecretKinds.GitHubToken, default));
        Assert.Equal("ph_token", await _store.GetPlaintextAsync("porthorizon", SecretKinds.GitHubToken, default));
    }
}
