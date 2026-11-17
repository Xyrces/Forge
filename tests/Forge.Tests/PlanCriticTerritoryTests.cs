using Forge.Agents.Gates;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class PlanCriticTerritoryTests
{
    [Fact]
    public async Task CriticReceivesEnforcedBoundaryAndDoesNotWaiveRequiredDocument()
    {
        var client = new CaptureClient();
        var context = new RunGateContext("client-1", "clientdev", ["Forge.UI/", "tests/"], false,
            TempRoot.Instance.NewDirectory("critic-territory"),
            "Implement UI and place the handoff in existing docs/evidence.md.",
            "## Files\n- Forge.UI/Widget.razor (new)\n## Done\nRun offline tests and record the handoff.", default);
        var result = await new PlanLlmReviewGate(() => client, NullLogger.Instance).EvaluateAsync(context);
        Assert.Equal(GateOutcome.Revise, result.Outcome);
        Assert.Contains("assignment conflict", result.Feedback);
        Assert.Contains("clientdev: allowed path prefixes: Forge.UI/, tests/; repo-root files: not allowed", client.Prompt);
        Assert.Contains(context.TaskText, client.Prompt);
        Assert.Contains("do not silently waive the deliverable", client.Prompt);
        Assert.Contains("not already-passing results", client.Prompt);
        Assert.Contains("A transcript does not replace that required document", client.Prompt);
        Assert.Contains("operator-owned", client.Prompt);
        // The additional critic context does not grant access to the explicitly required destination.
        var denied = await new PlanTerritoryGate().EvaluateAsync(context with
            { Plan = "## Files\n- docs/evidence.md (new)" });
        Assert.Equal(GateOutcome.Revise, denied.Outcome);
        Assert.Contains("outside clientdev's territory", denied.Feedback);
    }

    [Fact]
    public async Task UnconfiguredTerritoryIsNotReplacedWithForgeDefaults()
    {
        var client = new CaptureClient();
        await new PlanLlmReviewGate(() => client, NullLogger.Instance).EvaluateAsync(
            new("task-1", "coredev", [], false, "/unused", "Task", "Plan", default));
        Assert.Contains("coredev: no project-configured path restriction", client.Prompt);
        Assert.DoesNotContain("allowed path prefixes: Core/", client.Prompt);
    }

    private sealed class CaptureClient() : DelegatingChatClient(new MultiToolCallingChatClient([], "unused"))
    {
        public string Prompt { get; private set; } = "";
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompt = string.Join("\n", messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "The required document has an assignment conflict with the allowed territory; an authorized owner must resolve it.\nVERDICT: REVISE")));
        }
    }
}
