using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Forge.Core.Control;
using Forge.Core.Db;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Forge.Tests;

public sealed class ControlMigrationBarrierTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    [InlineData(null)]
    public async Task MigrationLockUnavailable_NeverReadsOrMutatesOwnership(int? result)
    {
        var factory = new LockedCatalog(result);
        var lease = await new ControlLeaseStore(factory).TryAcquireAsync("dataset", "pod", "activation");
        Assert.Null(lease);
        var query = Assert.Single(factory.Connection.Commands);
        Assert.Contains("forge-schema-migration", query);
        Assert.Contains("@LockMode='Shared'", query);
        Assert.Contains("@LockOwner='Transaction'", query);
        Assert.Contains("@LockTimeout=0", query);
        Assert.False(factory.Connection.Committed);
    }

    [Fact]
    public async Task DatabaseLossDuringBarrier_PropagatesWithoutOwnershipMutation()
    {
        var factory = new LockedCatalog(0) { Fail = true };
        await Assert.ThrowsAsync<IOException>(() =>
            new ControlLeaseStore(factory).TryAcquireAsync("dataset", "pod", "activation"));
        Assert.Single(factory.Connection.Commands);
        Assert.False(factory.Connection.Committed);
    }

    private sealed class LockedCatalog(int? result) : IDbConnectionFactory
    {
        public bool Fail { get; set; }
        public CatalogConnection Connection { get; } = new(result);
        public ForgeDbProvider Provider => ForgeDbProvider.SqlServer;
        public ISqlDialect Dialect => new SqlServerDialect("core");
        public ForgeSchemaProfile Profile => ForgeSchemaProfile.Core;
        public string ConnectionString => "fake";
        public string Qualifier => "core";
        public bool InitializeSchema => false;
        public DbConnection Open() { Connection.Fail = Fail; return Connection; }
        public Task<DbConnection> OpenAsync(CancellationToken ct = default) => Task.FromResult(Open());
    }

    private sealed class CatalogConnection(int? result) : DbConnection
    {
        public List<string> Commands { get; } = [];
        public bool Fail, Committed;
        [AllowNull] public override string ConnectionString { get; set; } = "fake";
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "fake";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new Transaction(this);
        protected override DbCommand CreateDbCommand() => new Command(this, result);

        private sealed class Transaction(CatalogConnection connection) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
            protected override DbConnection DbConnection => connection;
            public override void Commit() => connection.Committed = true;
            public override void Rollback() { }
        }

        private sealed class Command(CatalogConnection connection, int? result) : DbCommand
        {
            private readonly SqlCommand _parameters = new();
            [AllowNull] public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = connection;
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;
            protected override DbParameter CreateDbParameter() => new SqlParameter();
            public override void Cancel() { }
            public override void Prepare() { }
            public override int ExecuteNonQuery() => throw new Xunit.Sdk.XunitException("Ownership mutation happened behind the migration barrier.");
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
                throw new Xunit.Sdk.XunitException("Ownership read happened behind the migration barrier.");
            public override object? ExecuteScalar()
            {
                connection.Commands.Add(CommandText);
                if (connection.Fail) throw new IOException("Database unavailable");
                return result;
            }
        }
    }
}
