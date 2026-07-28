using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Db;

/// <summary>
/// Opens connections to the state database. One instance per (provider,
/// project-schema); shared by every store that hangs off the same logical
/// database (IssueStore + its sibling stores).
/// </summary>
public interface IDbConnectionFactory
{
    ForgeDbProvider Provider { get; }
    ISqlDialect Dialect { get; }

    /// <summary>Raw connection string (informational; SQLite tests use it to
    /// open raw verification connections).</summary>
    string ConnectionString { get; }

    /// <summary>Per-project schema qualifier. Empty on SQLite.</summary>
    string Qualifier { get; }

    Task<DbConnection> OpenAsync(CancellationToken ct = default);

    /// <summary>Synchronous open for constructor-time schema ensure.</summary>
    DbConnection Open();
}

public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    public SqliteConnectionFactory(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public ForgeDbProvider Provider => ForgeDbProvider.Sqlite;
    public ISqlDialect Dialect => SqliteDialect.Instance;
    public string ConnectionString { get; }
    public string Qualifier => "";

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public DbConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }
}

/// <summary>
/// SQL Server factory. Attaches the built-in configurable retry provider to
/// every connection (covers transient Azure SQL faults incl. 40613
/// "database not currently available" — the serverless auto-pause resume —
/// plus 10060/40197/40501 class errors); commands created from a connection
/// inherit its retry provider, so executes retry through the same policy.
/// Authentication comes from the connection string
/// (<c>Authentication=Active Directory Default</c>) — no secrets held here.
/// </summary>
public sealed class SqlServerConnectionFactory : IDbConnectionFactory
{
    private readonly SqlRetryLogicBaseProvider _retry;

    public SqlServerConnectionFactory(string connectionString, string qualifier)
    {
        ConnectionString = connectionString;
        Dialect = new SqlServerDialect(qualifier);
        _retry = SqlConfigurableRetryFactory.CreateExponentialRetryProvider(new SqlRetryLogicOption
        {
            // Generous window: the free-tier serverless dev DB resumes
            // from auto-pause in ~60s and login attempts fail with
            // 40613 / -2 timeouts until it's online. The Basic
            // production tier never pauses, so this only ever triggers
            // against dev or during Azure-side transient faults.
            NumberOfTries = 10,
            DeltaTime = TimeSpan.FromSeconds(3),
            MaxTimeInterval = TimeSpan.FromSeconds(90),
        });
    }

    public ForgeDbProvider Provider => ForgeDbProvider.SqlServer;
    public ISqlDialect Dialect { get; }
    public string ConnectionString { get; }
    public string Qualifier => ((SqlServerDialect)Dialect).Qualifier;

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = New();
        await conn.OpenAsync(ct);
        return conn;
    }

    public DbConnection Open()
    {
        var conn = New();
        conn.Open();
        return conn;
    }

    private SqlConnection New() => new(ConnectionString) { RetryLogicProvider = _retry };
}

/// <summary>Factory entry points. Store constructors that historically took a
/// SQLite file path keep working by delegating to <see cref="Sqlite(string)"/>.</summary>
public static class ForgeDb
{
    public static IDbConnectionFactory Sqlite(string connectionString)
        => new SqliteConnectionFactory(connectionString);

    public static IDbConnectionFactory SqlServer(string connectionString, string qualifier)
        => new SqlServerConnectionFactory(connectionString, qualifier);

    /// <summary>Schema name for a registered project's tables.</summary>
    public static string SchemaForProject(string projectId) => $"proj_{projectId}";

    /// <summary>
    /// Canonical per-project resolution used by every construction site
    /// (Program.FactoryFor, ProjectDispatchBundleFactory,
    /// ProjectContextFactory): SQL Server → shared database, schema
    /// proj_&lt;id&gt;; SQLite → per-project file (IssueStore's canonical
    /// builder settings).
    /// </summary>
    public static IDbConnectionFactory ForProject(
        bool isSqlServer, string connectionString, string projectId, string sqlitePath)
        => isSqlServer
            ? SqlServer(connectionString, SchemaForProject(projectId))
            : Sqlite(new SqliteConnectionStringBuilder
            {
                DataSource = sqlitePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Default,
                Pooling = true,
            }.ToString());

    /// <summary>Schema holding registry/global tables (project, secret, skill).</summary>
    public const string RegistrySchema = "dbo";
}
