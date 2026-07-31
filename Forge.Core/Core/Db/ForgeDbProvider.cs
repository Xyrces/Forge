namespace Forge.Core.Db;

/// <summary>
/// Which state-database backend a store talks to. SQLite remains the
/// default for tests and local dev; SQL Server (Azure SQL) is the
/// cloud primary. Selected by the <c>db.provider</c> config key.
/// </summary>
public enum ForgeDbProvider
{
    Sqlite,
    SqlServer,
}
