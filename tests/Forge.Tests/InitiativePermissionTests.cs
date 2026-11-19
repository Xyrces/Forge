using Forge.Orchestrator.Control;
using Xunit;
namespace Forge.Tests;
public sealed class InitiativePermissionTests
{
    [Fact]
    public void WorkerCannotWriteAnyApprovalAuthorityTableButRetainsReads()
    {
        var sql=ControllerInitializer.InitiativeWorkerPermissionsSql("worker-principal");
        foreach(var table in new[]{"initiative_proposal","initiative_revision","initiative_ingestion","initiative_lock","sprint","sprint_completion","deployment_environment_policy"})
        {
            Assert.Contains($"IF OBJECT_ID(N'proj_forge.{table}',N'U') IS NOT NULL",sql);
            Assert.Contains($"GRANT SELECT ON OBJECT::[proj_forge].[{table}] TO [worker-principal]",sql);
            Assert.Contains($"DENY INSERT,UPDATE,DELETE ON OBJECT::[proj_forge].[{table}] TO [worker-principal]",sql);
        }
        Assert.DoesNotContain("REVOKE",sql);
        Assert.DoesNotContain("controller",sql);
    }
    [Theory]
    [InlineData("")]
    [InlineData("worker];DROP TABLE x;--")]
    public void RejectsUnsafePrincipal(string principal)=>Assert.Throws<InvalidOperationException>(()=>ControllerInitializer.InitiativeWorkerPermissionsSql(principal));
}
