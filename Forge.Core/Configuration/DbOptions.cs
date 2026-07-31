namespace Forge.Configuration;

/// <summary>
/// State-database backend selection. <c>sqlite</c> (default) keeps the
/// per-project <c>.db</c> files under the data root; <c>sqlserver</c>
/// points every store at one Azure SQL database with schema-per-project
/// (proj_&lt;id&gt;). The connection string carries authentication
/// (<c>Authentication=Active Directory Default</c>) — never a password.
/// </summary>
public sealed class DbOptions
{
    public string Provider { get; set; } = "sqlite";

    /// <summary>SQL Server connection string (ignored for sqlite).</summary>
    public string ConnectionString { get; set; } = "";

    public bool IsSqlServer => string.Equals(Provider, "sqlserver", StringComparison.OrdinalIgnoreCase);
}
