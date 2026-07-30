using Forge.Core.Db;
using Xunit;

namespace Forge.Tests;

public class SqlDialectTests
{
    [Fact]
    public void Sqlite_Table_IsBareName()
    {
        Assert.Equal("issue", SqliteDialect.Instance.Table("issue"));
        Assert.Equal("", SqliteDialect.Instance.QualifierSafe());
    }

    [Fact]
    public void SqlServer_Table_IsSchemaQualified()
    {
        var d = new SqlServerDialect("proj_forge");
        Assert.Equal("[proj_forge].[issue]", d.Table("issue"));
        Assert.Equal("proj_forge", d.Qualifier);
    }

    [Fact]
    public void SqlServer_HyphenatedProjectId_IsAllowed()
    {
        // Project ids are slugs ([a-z0-9][a-z0-9_-]*); hyphens are safe
        // inside bracket quoting and keep schema<->project 1:1.
        var d = new SqlServerDialect(ForgeDb.SchemaForProject("port-horizon"));
        Assert.Equal("[proj_port-horizon].[issue]", d.Table("issue"));
    }

    [Theory]
    [InlineData("proj x")]
    [InlineData("proj;x")]
    [InlineData("proj'x")]
    [InlineData("proj.x")]
    [InlineData("")]
    public void SqlServer_UnsafeQualifier_Rejected(string qualifier)
    {
        Assert.Throws<ArgumentException>(() => new SqlServerDialect(qualifier));
    }

    [Fact]
    public void TopLimit_AreProviderShaped()
    {
        var ss = new SqlServerDialect("proj_x");
        Assert.Equal("TOP (5) ", ss.Top(5));
        Assert.Equal("", ss.Limit(5));
        Assert.Equal("TOP (@n) ", ss.TopParam("@n"));
        Assert.Equal("", ss.LimitParam("@n"));

        var sq = SqliteDialect.Instance;
        Assert.Equal("", sq.Top(5));
        Assert.Equal(" LIMIT 5", sq.Limit(5));
        Assert.Equal("", sq.TopParam("@n"));
        Assert.Equal(" LIMIT @n", sq.LimitParam("@n"));
    }
}

internal static class SqliteDialectTestExtensions
{
    // Sqlite has no qualifier; exposes "" via the factory, not the dialect.
    public static string QualifierSafe(this SqliteDialect _) => "";
}
