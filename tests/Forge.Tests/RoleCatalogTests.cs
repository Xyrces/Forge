using Forge.Agents;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// The canonical role catalog (operator rule 2026-07-24: the Agents
/// page and the project drill-down must answer "what agents exist?"
/// identically). Guards the shared lists both surfaces render from.
/// </summary>
public class RoleCatalogTests
{
    [Fact]
    public void AllSlotRoles_CoversEngineeringAndPipeline_ExactlyOnce()
    {
        var roles = RoleAgentRegistry.AllSlotRoles;

        // The four engineering roles (incl. qa — missing from the old
        // hand-maintained filler list) plus all six pipeline roles.
        Assert.Equal(10, roles.Count);
        Assert.Equal(roles.Count, roles.Distinct(StringComparer.Ordinal).Count());
        foreach (var expected in new[]
        {
            "coredev", "clientdev", "qa", "reviewer",
            "artist", "designer", "groomer", "intake", "orchestrator", "triage",
        })
        {
            Assert.Contains(expected, roles);
        }
    }

    [Fact]
    public void PipelineCatalog_ModelSemantics_AreExplicit()
    {
        // Intake and triage are the only pipeline roles with their own
        // AgentType — hence independently editable models.
        var intake = Assert.Single(RoleAgentRegistry.Pipeline, p => p.AgentName == "intake");
        Assert.Equal(Core.AgentType.Intake, intake.ModelType);
        var triage = Assert.Single(RoleAgentRegistry.Pipeline, p => p.AgentName == "triage");
        Assert.Equal(Core.AgentType.Triage, triage.ModelType);
        Assert.Null(triage.InheritsModelFrom);

        // Designer/groomer/artist borrow coredev's chat client; the
        // UI must SAY that instead of implying separate config.
        foreach (var name in new[] { "designer", "groomer", "artist" })
        {
            var role = Assert.Single(RoleAgentRegistry.Pipeline, p => p.AgentName == name);
            Assert.Null(role.ModelType);
            Assert.Equal("coredev", role.InheritsModelFrom);
        }

        // The orchestrator runs no LLM at all.
        var orch = Assert.Single(RoleAgentRegistry.Pipeline, p => p.AgentName == "orchestrator");
        Assert.Null(orch.ModelType);
        Assert.Null(orch.InheritsModelFrom);
    }
}
