using Forge.Agents;
using Forge.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// minimax-m3 occasionally emits its next tool call as literal text
/// markup ("]<]minimax[>[<tool_call>...") in the assistant content
/// instead of structured tool_calls, which ends the MAF loop
/// prematurely (zero edits, prose "final answer"). The runner detects
/// the leak and nudges the model to continue, bounded at 3 nudges.
/// </summary>
public class MafAgentRunnerLeakedMarkupTests
{
    [Theory]
    [InlineData("]<]minimax[><tool_call><invoke name=\"bash\">", true)]
    [InlineData("some prose <tool_call> ...", true)]
    [InlineData("prose <invoke name=\"bash\"> ...", true)]
    [InlineData("Implemented the endpoint in DashboardHost.cs", false)]
    [InlineData("", false)]
    public void HasLeakedToolCallMarkup_DetectsMarkers(string text, bool expected)
    {
        Assert.Equal(expected, MafAgentRunner.HasLeakedToolCallMarkup(text));
    }

    /// <summary>
    /// Scripted client: first response is leaked markup (no structured
    /// tool calls, so MAF ends its loop); subsequent responses are clean.
    /// </summary>
    private sealed class LeakThenCleanClient : IChatClient
    {
        public int CallCount;
        private readonly bool _alwaysLeak;
        public LeakThenCleanClient(bool alwaysLeak = false) { _alwaysLeak = alwaysLeak; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var leak = _alwaysLeak || CallCount == 1;
            var text = leak
                ? "Let me check the role rules:]<]minimax[><tool_call><invoke name=\"bash\"><command>cat agents/coredev.md</command>"
                : "Implemented the endpoint in DashboardHost.cs";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ScriptingFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public ScriptingFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null) => _client;
    }

    private static MafAgentRunner NewRunner(IChatClient client) => new(
        chatClientFactory: new ScriptingFactory(client),
        config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
        roles: new RoleAgentRegistry(),
        logger: NullLogger<MafAgentRunner>.Instance,
        skills: null,
        rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-leak-md-{Guid.NewGuid():N}"));

    [Fact]
    public async Task RunAsync_LeakedMarkup_NudgesModelAndReturnsCleanResponse()
    {
        var client = new LeakThenCleanClient();
        var runner = NewRunner(client);

        var result = await runner.RunAsync(AgentType.CoreDev, "implement the endpoint", sessionId: null, ct: default);

        Assert.Equal(2, client.CallCount);
        Assert.Equal("Implemented the endpoint in DashboardHost.cs", result.Text);
    }

    [Fact]
    public async Task RunAsync_PersistentLeak_IsBoundedAtThreeNudges()
    {
        var client = new LeakThenCleanClient(alwaysLeak: true);
        var runner = NewRunner(client);

        var result = await runner.RunAsync(AgentType.CoreDev, "implement the endpoint", sessionId: null, ct: default);

        // 1 initial run + 3 continuation nudges, then the runner gives
        // up and returns what it has (the downstream no-diff handling
        // drains the task; the diag log carries the evidence).
        Assert.Equal(4, client.CallCount);
        Assert.Contains("]<]minimax[>", result.Text);
    }
}
