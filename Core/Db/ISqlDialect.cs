namespace Forge.Core.Db;

/// <summary>
/// Provider-specific SQL surface. Stores write portable SQL and route the
/// (few) divergent constructs through this dialect. Deliberately small:
/// upserts and RETURNING/OUTPUT are branched per call-site instead of
/// hidden behind clever abstractions — the divergence is visible in the
/// store where a reviewer can check it.
///
/// Conventions shared by both providers:
/// - Parameters are always <c>@name</c> (SQLite accepts @/$/:; SqlClient
///   only @ — so @ is the portable form).
/// - Dates are C#-formatted strings ("yyyy-MM-dd HH:mm:ss.fff") in
///   TEXT/NVARCHAR columns; ordering is lexicographic and correct for
///   that format. No provider date functions appear in queries.
/// - Integer columns are INTEGER (SQLite) / BIGINT (SQL Server); reads
///   use GetInt64, which both providers satisfy.
/// </summary>
public interface ISqlDialect
{
    ForgeDbProvider Provider { get; }

    /// <summary>
    /// Fully-qualified table reference. SQLite: bare name. SQL Server:
    /// <c>[qualifier].[name]</c> where qualifier is the per-project schema
    /// (dbo for the registry project).
    /// </summary>
    string Table(string name);

    /// <summary>SELECT-prefix row cap: <c>TOP (n)</c> on SQL Server, empty on SQLite.</summary>
    string Top(int n);

    /// <summary>Statement-suffix row cap: <c> LIMIT n</c> on SQLite, empty on SQL Server.</summary>
    string Limit(int n);

    /// <summary>SELECT-prefix row cap bound to a parameter: <c>TOP (@p)</c> or empty.</summary>
    string TopParam(string param);

    /// <summary>Statement-suffix row cap bound to a parameter: <c> LIMIT @p</c> or empty.</summary>
    string LimitParam(string param);

    // DDL type fragments (SQL Server DDL is hand-written fresh-create at the
    // current schema version; these keep the two DDL blocks in each store
    // visually parallel).
    string TextType { get; }
    string TextKeyType { get; }
    string IntType { get; }
    string RealType { get; }

    /// <summary>Auto-increment integer primary key column fragment.</summary>
    string IdentityPk { get; }
}

public sealed class SqliteDialect : ISqlDialect
{
    public static readonly SqliteDialect Instance = new();
    private SqliteDialect() { }

    public ForgeDbProvider Provider => ForgeDbProvider.Sqlite;
    public string Table(string name) => name;
    public string Top(int n) => "";
    public string Limit(int n) => $" LIMIT {n}";
    public string TopParam(string param) => "";
    public string LimitParam(string param) => $" LIMIT {param}";
    public string TextType => "TEXT";
    public string TextKeyType => "TEXT";
    public string IntType => "INTEGER";
    public string RealType => "REAL";
    public string IdentityPk => "INTEGER PRIMARY KEY AUTOINCREMENT";
}

public sealed class SqlServerDialect : ISqlDialect
{
    private readonly string _qualifier;

    /// <param name="qualifier">Schema name (dbo, proj_&lt;id&gt;). Must already be
    /// validated to a safe identifier ([a-z0-9_]) by the caller — project ids
    /// are lowercase slugs, so <c>proj_{id}</c> is safe by construction.</param>
    public SqlServerDialect(string qualifier)
    {
        if (string.IsNullOrWhiteSpace(qualifier))
            throw new ArgumentException("Schema qualifier is required for SQL Server.", nameof(qualifier));
        foreach (var c in qualifier)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                throw new ArgumentException($"Schema qualifier '{qualifier}' contains unsafe character '{c}'.", nameof(qualifier));
        }
        _qualifier = qualifier;
    }

    public string Qualifier => _qualifier;
    public ForgeDbProvider Provider => ForgeDbProvider.SqlServer;
    public string Table(string name) => $"[{_qualifier}].[{name}]";
    public string Top(int n) => $"TOP ({n}) ";
    public string Limit(int n) => "";
    public string TopParam(string param) => $"TOP ({param}) ";
    public string LimitParam(string param) => "";
    public string TextType => "NVARCHAR(MAX)";
    // 128 keeps composite index keys under the 900-byte clustered /
    // 1700-byte nonclustered SQL Server limits (real ids are <=40 chars).
    public string TextKeyType => "NVARCHAR(128)";
    public string IntType => "BIGINT";
    public string RealType => "FLOAT";
    public string IdentityPk => "BIGINT IDENTITY(1,1) PRIMARY KEY";
}
