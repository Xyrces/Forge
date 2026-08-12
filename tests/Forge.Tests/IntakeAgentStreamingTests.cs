using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// The intake chat page renders a live assistant bubble from
/// intake.run.delta / intake.run.tool SSE events (2026-08-12
/// live-feedback rework). These tests pin the contract the page
/// depends on: deltas stream in order and concatenate to the
/// persisted assistant message, and providers that can't stream
/// fall back to a buffered run.
/// </summary>
public class IntakeAgentStreamingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly IntakeStore _intake;
    private readonly SprintStore _sprints;
    private readonly List<DashboardEvent> _events = new();
    private readonly IDashboardEventBus _bus;

    public IntakeAgentStreamingTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("intake-stream");
        _issues = new IssueStore(_dbPath);
        _intake = new IntakeStore(_issues);
        _sprints = new SprintStore(_issues);
        _bus = new CapturingBus(_events);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private IntakeAgent NewAgent(IChatClient client) => new(
        projectId: "proj",
        intakeStore: _intake,
        issues: _issues,
        sprints: _sprints,
        chatClientFactory: new FixedFactory(client),
        config: new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
        roles: new RoleAgentRegistry(),
        events: _bus,
        logger: NullLogger<IntakeAgent>.Instance);

    [Fact]
    public async Task SendUserMessage_StreamsDeltas_ConcatEqualsPersistedAssistantMessage()
    {
        var client = new ChunkedStreamingClient("Hello, ", "operator", ".");
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var updated = await agent.SendUserMessageAsync(session.Id, "hi", default);

        var deltas = _events.Where(e => e.Kind == DashboardEventKind.IntakeRunDelta)
            .Select(e => e.Data?["delta"] as string)
            .ToList();
        Assert.Equal(new[] { "Hello, ", "operator", "." }, deltas);

        var assistant = updated.Messages.Last(m => m.Role == IntakeMessageRole.Assistant);
        Assert.Equal("Hello, operator.", assistant.Content);
        Assert.Equal(string.Concat(deltas), assistant.Content);

        Assert.Contains(_events, e => e.Kind == DashboardEventKind.IntakeRunStarted);
        Assert.Contains(_events, e => e.Kind == DashboardEventKind.IntakeRunCompleted);
    }

    [Fact]
    public async Task SendUserMessage_NonStreamingProvider_FallsBackToBufferedRun()
    {
        var client = new NonStreamingClient("buffered reply");
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var updated = await agent.SendUserMessageAsync(session.Id, "hi", default);

        Assert.DoesNotContain(_events, e => e.Kind == DashboardEventKind.IntakeRunDelta);
        var assistant = updated.Messages.Last(m => m.Role == IntakeMessageRole.Assistant);
        Assert.Equal("buffered reply", assistant.Content);
    }

    [Fact]
    public async Task SendUserMessage_AskQuestionTool_AttachesQuestionsToAssistantMessage()
    {
        var client = new QuestionCallingClient();
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var updated = await agent.SendUserMessageAsync(session.Id, "add a transport", default);

        var assistant = updated.Messages.Last(m => m.Role == IntakeMessageRole.Assistant);
        Assert.NotNull(assistant.Questions);
        Assert.Equal(2, assistant.Questions!.Count);
        Assert.Equal("Which scope?", assistant.Questions[0].Question);
        Assert.Equal(new[] { "Transport only", "Transport + outbox" }, assistant.Questions[0].Options);
        Assert.Equal("Anything else to pin down?", assistant.Questions[1].Question);
        // The tool announced itself over the live channel.
        Assert.Contains(_events, e => e.Kind == DashboardEventKind.IntakeRunTool);
    }

    [Fact]
    public async Task SendUserMessage_TextQuestions_WithoutTool_FallsBackToParsing()
    {
        var client = new ChunkedStreamingClient("Before I propose:\n\n1. **Scope** — Transport only or also the outbox?\n   - Transport only\n   - Transport + outbox\n");
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var updated = await agent.SendUserMessageAsync(session.Id, "hi", default);

        var assistant = updated.Messages.Last(m => m.Role == IntakeMessageRole.Assistant);
        var q = Assert.Single(assistant.Questions!);
        Assert.Contains("Scope", q.Question);
        Assert.Equal(2, q.Options.Count);
    }

    /// <summary>Calls ask_question once per question (the flat-args
    /// shape), then returns an empty-text follow-up — exercising the
    /// placeholder content for tool-only replies.</summary>
    private sealed class QuestionCallingClient : IChatClient
    {
        private int _callIndex;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("streaming only");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_callIndex++ == 0)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    new AIContent[]
                    {
                        new FunctionCallContent("call_1", "ask_question",
                            new Dictionary<string, object?>
                            {
                                ["question"] = "Which scope?",
                                ["options"] = System.Text.Json.JsonSerializer.SerializeToElement(
                                    new[] { "Transport only", "Transport + outbox" }),
                            }),
                        new FunctionCallContent("call_2", "ask_question",
                            new Dictionary<string, object?>
                            {
                                ["question"] = "Anything else to pin down?",
                            }),
                    });
                yield break;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FixedFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public FixedFactory(IChatClient client) => _client = client;
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null) => _client;
    }

    private sealed class CapturingBus : IDashboardEventBus
    {
        private readonly List<DashboardEvent> _sink;
        public CapturingBus(List<DashboardEvent> sink) => _sink = sink;
        public void Publish(DashboardEvent ev) { lock (_sink) _sink.Add(ev); }
        public ChannelReader<DashboardEvent> Subscribe() =>
            Channel.CreateUnbounded<DashboardEvent>().Reader;
    }

    /// <summary>Yields each scripted chunk as its own streaming update;
    /// the buffered path returns the concatenation.</summary>
    private sealed class ChunkedStreamingClient : IChatClient
    {
        private readonly string[] _chunks;
        public ChunkedStreamingClient(params string[] chunks) => _chunks = chunks;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(_chunks))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var c in _chunks)
                yield return new ChatResponseUpdate(ChatRole.Assistant, c);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Streaming throws NotSupportedException — the intake
    /// agent must fall back to the buffered call.</summary>
    private sealed class NonStreamingClient : IChatClient
    {
        private readonly string _reply;
        public NonStreamingClient(string reply) => _reply = reply;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("streaming not supported");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
