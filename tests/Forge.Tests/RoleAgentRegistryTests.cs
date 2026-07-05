using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class RoleAgentRegistryTests
{
    [Fact]
    public void ForType_KnownTypes_ReturnRoleAgents()
    {
        var registry = new RoleAgentRegistry();
        var coredev = registry.ForType(AgentType.CoreDev);
        Assert.Equal("coredev", coredev.KiloAgentName);
        Assert.Equal("PortHorizon.Core", coredev.ProjectSubdir);

        var reviewer = registry.ForType(AgentType.Reviewer);
        Assert.Equal("reviewer", reviewer.KiloAgentName);
        Assert.DoesNotContain(reviewer.AllowedTools, t => t == "edit");
        Assert.DoesNotContain(reviewer.AllowedTools, t => t == "bash");
    }

    [Fact]
    public void ForType_UnknownType_Throws()
    {
        var registry = new RoleAgentRegistry();
        Assert.Throws<InvalidOperationException>(() => registry.ForType(AgentType.Orchestrator));
    }

    [Theory]
    [InlineData("ecs", AgentType.CoreDev)]
    [InlineData("atmospherics", AgentType.CoreDev)]
    [InlineData("client", AgentType.ClientDev)]
    [InlineData("ui", AgentType.ClientDev)]
    [InlineData("godot", AgentType.ClientDev)]
    [InlineData("test", AgentType.QA)]
    [InlineData("qa", AgentType.QA)]
    [InlineData("review", AgentType.Reviewer)]
    [InlineData("unknown", AgentType.CoreDev)]
    public void FromTaskType_MapsCorrectly(string taskType, AgentType expected)
    {
        Assert.Equal(expected, RoleAgentRegistry.FromTaskType(taskType));
    }
}