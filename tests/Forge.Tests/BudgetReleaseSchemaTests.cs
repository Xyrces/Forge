using System.Data.Common;
using Forge.Core;
using Forge.Core.Db;
using Forge.Orchestrator.Control;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Tests;

public sealed class BudgetReleaseSchemaTests
{
    [Theory]
    [InlineData(38, false)]
    [InlineData(39, false)]
    [InlineData(40, false)]
    [InlineData(41, false)]
    [InlineData(42, false)]
    [InlineData(43, true)]
    [InlineData(44, false)]
    [InlineData(0, false)]
    public async Task CurrentReleaseRequires43AndCannotInheritBridgeCompatibility(int version, bool allowed)
    {
        Assert.Equal(43, IssueStore.CurrentSchemaVersion);
        Assert.Equal(43, SqlServerMigrations.ExpectedVersion);
        var path = TempRoot.Instance.NewDbPath("budget-release-schema");
        await using (var setup = new SqliteConnection($"Data Source={path}"))
        {
            await setup.OpenAsync();
            using var create = setup.CreateCommand();
            create.CommandText = "CREATE TABLE schema_version(version INTEGER PRIMARY KEY)";
            await create.ExecuteNonQueryAsync();
            if (version != 0)
            {
                create.CommandText = "INSERT INTO schema_version(version) VALUES(@version)";
                create.Parameters.AddWithValue("@version", version);
                await create.ExecuteNonQueryAsync();
            }
        }
        var factory = new ReadOnlyCatalogFactory(path);
        await using var runtimeStore = new IssueStore(factory);
        runtimeStore.EnsureSchema(); // runtime construction must not initialize even this minimal fixture
        if (allowed) Assert.Equal(43, await ControllerHost.VerifySchemaAsync(factory, default));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => ControllerHost.VerifySchemaAsync(factory, default));
        await using var connection = await factory.OpenAsync();
        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_version";
        Assert.Equal(version, Convert.ToInt32(await verify.ExecuteScalarAsync()));
        verify.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
        Assert.Equal(1L, Convert.ToInt64(await verify.ExecuteScalarAsync()));
    }

    // Actual read-only database validates the SQL catalog query shape without Azure access.
    private sealed class ReadOnlyCatalogFactory(string path) : IDbConnectionFactory
    {
        private readonly SqliteConnectionFactory inner = new(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        public bool InitializeSchema => false;
        public ForgeDbProvider Provider => ForgeDbProvider.SqlServer;
        public ISqlDialect Dialect { get; } = new SqlServerDialect("main");
        public ForgeSchemaProfile Profile => ForgeSchemaProfile.Core;
        public string ConnectionString => inner.ConnectionString;
        public string Qualifier => "main";
        public DbConnection Open() => inner.Open();
        public Task<DbConnection> OpenAsync(CancellationToken ct = default) => inner.OpenAsync(ct);
    }
}
