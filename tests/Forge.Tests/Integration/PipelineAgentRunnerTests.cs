using Forge.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// <see cref="PipelineAgentRunner"/>: the single-shot continuation loop
/// shared by the pipeline agents. Leaked-markup nudges and
/// required-tool (contract) nudges share ONE budget of 3; healthy runs
/// are never nudged.
/// </summary>
public class PipelineAgentRunnerTests
{
    private const string LeakText =
        "Let me check:]<]minimax[><tool_call><invoke name=\"bash\"><command>ls</command>";

    /// <summary>
    /// Responds from a scripted queue (one entry per GetResponseAsync
    /// call); repeats the last entry when the queue drains.
    /// </summary>
    private sealed class SequenceClient : IChatClient
    {
        private readonly ChatResponse[] _responses;
        public int CallCount;
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = new();

        public SequenceClient(params ChatResponse[] responses) => _responses = responses;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            var idx = Math.Min(CallCount, _responses.Length - 1);
            CallCount++;
            return Task.FromResult(_responses[idx]);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatResponse Text(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    private static ChatResponse Call(string name, Dictionary<string, object?>? args = null) =>
        new(new ChatMessage(ChatRole.Assistant,
            new AIContent[] { new FunctionCallContent("c1", name, args ?? new()) }));

    private static ChatClientAgent NewAgent(IChatClient inner, params AITool[] tools)
    {
        var client = new ChatClientBuilder(inner).UseFunctionInvocation().Build();
        return new ChatClientAgent(client, instructions: "test agent", tools: tools);
    }

    private static PipelineAgentRunner NewRunner() =>
        new(NullLogger<PipelineAgentRunner>.Instance);

    [Fact]
    public async Task HappyPath_CleanResponse_NoNudges()
    {
        var client = new SequenceClient(Text("done"));
        var agent = NewAgent(client);

        var outcome = await NewRunner().RunAsync(
            agent, new[] { new ChatMessage(ChatRole.User, "prompt") }, roleLabel: "test");

        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, outcome.TotalNudges);
        Assert.False(outcome.BudgetExhausted);
        Assert.Equal("done", outcome.FinalResponse.Text);
    }

    [Fact]
    public async Task LeakedMarkup_NudgesAndRecovers()
    {
        var client = new SequenceClient(Text(LeakText), Text("Implemented the thing."));
        var agent = NewAgent(client);

        var outcome = await NewRunner().RunAsync(
            agent, new[] { new ChatMessage(ChatRole.User, "prompt") }, roleLabel: "designer");

        Assert.Equal(2, client.CallCount);
        Assert.Equal(1, outcome.LeakNudges);
        Assert.Equal(0, outcome.ContractNudges);
        Assert.False(outcome.BudgetExhausted);
        Assert.Equal("Implemented the thing.", outcome.FinalResponse.Text);
        // NewMessages carries both assistant rounds + the nudge, in order.
        Assert.Equal(3, outcome.NewMessages.Count);
        Assert.Equal(ChatRole.User, outcome.NewMessages[1].Role);
        Assert.Equal(LeakedToolCallMarkup.ContinuationPrompt, outcome.NewMessages[1].Text);
    }

    [Fact]
    public async Task PersistentLeak_BudgetExhausted()
    {
        var client = new SequenceClient(Text(LeakText));
        var agent = NewAgent(client);

        var outcome = await NewRunner().RunAsync(
            agent, new[] { new ChatMessage(ChatRole.User, "prompt") }, roleLabel: "groomer");

        // 1 initial run + 3 leak nudges, then the loop gives up.
        Assert.Equal(4, client.CallCount);
        Assert.Equal(3, outcome.LeakNudges);
        Assert.True(outcome.BudgetExhausted);
        Assert.Contains("]<]minimax[>", outcome.FinalResponse.Text);
    }

    [Fact]
    public async Task ContractNudge_MissingRequiredTool_NudgesUntilCalled()
    {
        var toolInvocations = 0;
        var setStatus = AIFunctionFactory.Create(
            () => { toolInvocations++; return "ok"; },
            name: "db_set_spec_status");
        // Round 1: prose, no tool. Round 2 (after nudge): the tool call.
        var client = new SequenceClient(Text("I designed it, all done."), Call("db_set_spec_status"), Text("committed"));
        var agent = NewAgent(client, setStatus);

        var outcome = await NewRunner().RunAsync(
            agent, new[] { new ChatMessage(ChatRole.User, "prompt") }, roleLabel: "designer",
            requiredToolName: "db_set_spec_status");

        Assert.Equal(1, toolInvocations);
        Assert.Equal(1, outcome.ContractNudges);
        Assert.Equal(0, outcome.LeakNudges);
        Assert.False(outcome.BudgetExhausted);
    }

    [Fact]
    public async Task ContractNudge_NeverCalled_BudgetExhausted()
    {
        var client = new SequenceClient(Text("no tool call, ever"));
        var agent = NewAgent(client);

        var outcome = await NewRunner().RunAsync(
            agent, new[] { new ChatMessage(ChatRole.User, "prompt") }, roleLabel: "designer",
            requiredToolName: "db_set_spec_status");

        Assert.Equal(4, client.CallCount);
        Assert.Equal(3, outcome.ContractNudges);
        Assert.True(outcome.BudgetExhausted);
    }

    [Fact]
    public async Task SharedBudget_LeakAndContractNudgesDrawFromOneCounter()
    {
        var client = new SequenceClient(
            Text(LeakText),          // round 1 -> leak nudge (1)
            Text(LeakText),          // round 2 -> leak nudge (2)
            Text("clean, no tool"),  // round 3 -> contract nudge (3 = budget)
            Text("clean, no tool")); // round 4 -> budget spent, stop
        var agent = NewAgent(client);

        var outcome = await NewRunner().RunAsync(
            agent, new[] { new ChatMessage(ChatRole.User, "prompt") }, roleLabel: "designer",
            requiredToolName: "db_set_spec_status");

        Assert.Equal(4, client.CallCount);
        Assert.Equal(2, outcome.LeakNudges);
        Assert.Equal(1, outcome.ContractNudges);
        Assert.Equal(3, outcome.TotalNudges);
        Assert.True(outcome.BudgetExhausted);
    }

    [Fact]
    public async Task ToolCalledInEarlierRound_ThenLeak_ClearsContract()
    {
        // The required tool was called in round 1, but round 1 also
        // leaked markup (leak wins the nudge). After the leak nudge the
        // model answers clean prose: the contract is ALREADY satisfied
        // by round 1's call, so no contract nudge fires.
        var toolInvocations = 0;
        var setStatus = AIFunctionFactory.Create(
            () => { toolInvocations++; return "ok"; },
            name: "db_set_spec_status");
        var client = new SequenceClient(
            Call("db_set_spec_status"),
            Text(LeakText),
            Text("wrapped up"));
        var agent = NewAgent(client, setStatus);

        var outcome = await NewRunner().RunAsync(
            agent, new[] { new ChatMessage(ChatRole.User, "prompt") }, roleLabel: "designer",
            requiredToolName: "db_set_spec_status");

        Assert.Equal(1, toolInvocations);
        Assert.Equal(1, outcome.LeakNudges);
        Assert.Equal(0, outcome.ContractNudges);
        Assert.False(outcome.BudgetExhausted);
    }

    [Fact]
    public async Task HistoryShapedInput_FullConversationAccepted()
    {
        var client = new SequenceClient(Text("reply"));
        var agent = NewAgent(client);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "first question"),
            new(ChatRole.Assistant, "first answer"),
            new(ChatRole.User, "second question"),
        };

        var outcome = await NewRunner().RunAsync(agent, history, roleLabel: "intake");

        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, outcome.TotalNudges);
        // The full caller-supplied history reached the client.
        Assert.Equal(3, client.Requests[0].Count);
        Assert.Equal("second question", client.Requests[0][2].Text);
    }

    [Fact]
    public void FinalTextExcerpt_TruncatesAtMax()
    {
        var longText = new string('x', 900);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "prompt"),
            new(ChatRole.Assistant, longText),
        };

        var excerpt = PipelineAgentRunner.FinalTextExcerpt(messages);

        Assert.Equal(500, excerpt.Length);
        Assert.Equal(longText[..500], excerpt);
    }
}
