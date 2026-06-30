using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Configuration;
using Xunit;

namespace PortHorizon.Agents.Tests;

public class AgentOptionsTests
{
    [Fact]
    public void Default_LlmOptions_AreStub()
    {
        // Default Provider=Stub keeps a fresh orchestrator from accidentally
        // hitting a real LLM endpoint.
        var options = new AgentOptions();
        Assert.Equal("Stub", options.Llm.Provider);
        Assert.Equal("stub-model", options.Llm.Model);
        Assert.Equal(string.Empty, options.Llm.ApiKey);
        Assert.Equal(string.Empty, options.Llm.OrgId);
    }

    [Fact]
    public void Default_AgentOptions_AreSafe()
    {
        // No required fields are null; an orchestrator with all defaults
        // boots without throwing (a real config would set Workspace.Root
        // and GitHub.* via appsettings.json).
        var options = new AgentOptions();
        Assert.NotNull(options.Workspace);
        Assert.NotNull(options.GitHub);
        Assert.NotNull(options.Spawner);
        Assert.NotNull(options.Dashboard);
        Assert.NotNull(options.Llm);
    }

    [Fact]
    public void AgentOptions_OverrideLlmOptions()
    {
        var options = new AgentOptions
        {
            Llm = new LlmOptions
            {
                Provider = LlmProviders.OpenAI,
                Model = "gpt-5",
                ApiKey = "sk-abc",
                OrgId = "org-1",
            },
        };
        Assert.Equal(LlmProviders.OpenAI, options.Llm.Provider);
        Assert.Equal("gpt-5", options.Llm.Model);
        Assert.Equal("sk-abc", options.Llm.ApiKey);
        Assert.Equal("org-1", options.Llm.OrgId);
    }

    [Fact]
    public void LlmProviders_ConstantsAreDistinct()
    {
        // Guard against accidentally aliasing two providers to the same string.
        var all = new[]
        {
            LlmProviders.Stub,
            LlmProviders.OpenAI,
            LlmProviders.Anthropic,
            LlmProviders.GitHubCopilot,
            LlmProviders.Foundry,
        };
        Assert.Equal(all.Length, all.Distinct().Count());
    }
}
