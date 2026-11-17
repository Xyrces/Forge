using Forge.Orchestrator.Control;
using Xunit;

namespace Forge.Tests;

public sealed class ReconciliationPermissionTests
{
    [Fact]
    public void RuntimeIdentitiesCanReadButCannotAlterReconciliationEvidence()
    {
        var sql = ControllerInitializer.BudgetReconciliationPermissionsSql("runtime-principal", "worker-principal");
        foreach (var principal in new[] { "runtime-principal", "worker-principal" })
        foreach (var table in new[] { "budget_reconciliation", "budget_reconciliation_request" })
        {
            Assert.Contains($"GRANT SELECT ON OBJECT::[core].[{table}] TO [{principal}];", sql);
            Assert.Contains($"DENY INSERT,UPDATE,DELETE ON OBJECT::[core].[{table}] TO [{principal}];", sql);
        }
        Assert.DoesNotContain("GRANT INSERT", sql);
        Assert.DoesNotContain("GRANT UPDATE", sql);
        Assert.DoesNotContain("GRANT DELETE", sql);
        Assert.DoesNotContain("ON SCHEMA", sql);
        Assert.DoesNotContain("budget_reservation]", sql);
    }

    [Theory]
    [InlineData("", "worker")]
    [InlineData("runtime", "")]
    [InlineData("bad]; DROP TABLE x;--", "worker")]
    [InlineData("runtime", "bad name")]
    [InlineData("same", "SAME")]
    public void InvalidOrSharedPrincipalsAreRejectedBeforeSql(string controller, string worker) =>
        Assert.Throws<InvalidOperationException>(() =>
            ControllerInitializer.BudgetReconciliationPermissionsSql(controller, worker));
}
