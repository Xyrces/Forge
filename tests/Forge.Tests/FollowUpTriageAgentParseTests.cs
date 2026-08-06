using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class FollowUpTriageAgentParseTests
{
    [Fact]
    public void Parse_ValidContract_AllActions()
    {
        var json = """
            [
              {"action":"merge","title":"merged thing","description":"both","priority":2,"sources":[1,7]},
              {"action":"epic","title":"theme","description":"group","sources":[2,3,4]},
              {"action":"create","title":"unique","priority":3,"sources":[5]},
              {"action":"discard","reason":"junk","sources":[6]}
            ]
            """;
        var decision = FollowUpTriageAgent.Parse(json);
        Assert.NotNull(decision);
        Assert.Equal(4, decision!.Items.Count);
        Assert.Equal("merge", decision.Items[0].Action);
        Assert.Equal(new long[] { 1, 7 }, decision.Items[0].SourceDraftIds);
        Assert.Equal("junk", decision.Items[3].Reason);
    }

    [Fact]
    public void Parse_ProseWrapped_ExtractsArray()
    {
        var decision = FollowUpTriageAgent.Parse(
            "Here is my triage:\n[{\"action\":\"create\",\"title\":\"x\",\"sources\":[9]}]\nHope that helps.");
        Assert.NotNull(decision);
        Assert.Single(decision!.Items);
    }

    [Fact]
    public void Parse_Garbage_ReturnsNull()
    {
        Assert.Null(FollowUpTriageAgent.Parse("no json here"));
        Assert.Null(FollowUpTriageAgent.Parse("[{not valid}]"));
    }

    [Fact]
    public void Parse_UnknownActionAndEmptySources_Skipped()
    {
        var decision = FollowUpTriageAgent.Parse("""
            [
              {"action":"explode","title":"x","sources":[1]},
              {"action":"create","title":"y","sources":[]},
              {"action":"create","title":"z","sources":[2]}
            ]
            """);
        Assert.NotNull(decision);
        var item = Assert.Single(decision!.Items);
        Assert.Equal("z", item.Title);
    }
}
