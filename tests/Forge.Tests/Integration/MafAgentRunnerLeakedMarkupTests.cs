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

    [Fact]
    public void JoinAssistantText_NewlineJoins_SoLineContractsSurvive()
    {
        // Regression (2026-08-24, task-740): the runner assembled
        // AgentRunResult.Text with string.Concat — an intermediate
        // assistant prose message (no trailing newline) fused with the
        // final message's first line, and the QA_VERDICT marker was no
        // longer at a line start. Two full pass verdicts vanished as
        // "no QA_VERDICT marker in the run's final message".
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, "4630411 is a test-results-only refresh. Running the harness:"),
            new ChatMessage(ChatRole.Tool, "ok"),
            new ChatMessage(ChatRole.Assistant, "QA_VERDICT: pass\nplayed the build; evidence captured"),
        };

        var text = MafAgentRunner.JoinAssistantText(messages);

        var (verdict, notes) = Forge.Reviewer.QaDispatcher.ParseQaOutput(text);
        Assert.Equal(Forge.Reviewer.QaDispatcher.VerdictPass, verdict);
        Assert.Contains("played the build", notes);
    }

    [Fact]
    public void JoinAssistantText_SkipsNonAssistantMessages()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "the prompt"),
            new ChatMessage(ChatRole.Assistant, "first"),
            new ChatMessage(ChatRole.Tool, "tool output"),
            new ChatMessage(ChatRole.Assistant, "second"),
        };

        Assert.Equal("first\nsecond", MafAgentRunner.JoinAssistantText(messages));
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
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => _client;
    }

    private static MafAgentRunner NewRunner(IChatClient client) => new(
        chatClientFactory: new ScriptingFactory(client),
        config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
        roles: new RoleAgentRegistry(),
        logger: NullLogger<MafAgentRunner>.Instance,
        skills: null,
        rolePromptsRoot: TempRoot.Instance.NewDirectory("leak-md"));

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
