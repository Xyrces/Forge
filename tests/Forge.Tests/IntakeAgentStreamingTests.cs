using Forge.Core.Initiatives;
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
        _issues = new IssueStore(_dbPath,"proj");
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
        logger: NullLogger<IntakeAgent>.Instance, initiatives: Approval());

    private InitiativeProposalStore Proposals => new(_issues);
    private InitiativeApprovalService Approval()=>new(Proposals,_=>new InitiativeIngestionStore(_issues),_bus);
    private async Task<InitiativeProposal> Latest()=> (await Proposals.GetAsync("proj",(await Proposals.ListAsync("proj")).Last().Id))!;

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
        Assert.Equal("Transport scope", assistant.Questions[0].Header);
        Assert.False(assistant.Questions[0].Multiple);
        Assert.Equal(new[] { "Transport only", "Transport + outbox" }, assistant.Questions[0].Options);
        Assert.Equal("Anything else to pin down?", assistant.Questions[1].Question);
        Assert.True(assistant.Questions[1].Multiple);
        Assert.Empty(assistant.Questions[1].Options);
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

    [Fact]
    public async Task SendUserMessage_ToolOnlyEpicTurn_PersistsProposalPlaceholder_NotCrash()
    {
        // Live incident 2026-08-14: the model answered "go ahead" with
        // a create_epic call and NO text; the empty assistantText hit
        // IntakeStore.AppendMessageAsync's content guard and the
        // endpoint 500'd. The proposal placeholder keeps the turn.
        var client = new TurnScriptedClient()
            .Turn(FunctionCall("create_epic", "call_1",
                ("title", "ASB transport"), ("description", "first draft"), ("priority", 2)))
            .Turn(); // empty follow-up: no text after the tool result
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var updated = await agent.SendUserMessageAsync(session.Id, "go ahead", default);

        // The proposal is a session draft — NO issue row exists until
        // the operator accepts (operator rule 2026-08-14).
        Assert.Empty(await _issues.ListAsync(new IssueFilter { Assignee = "intake" }, default));
        var draft = await Latest();
        Assert.Equal("ASB transport", draft.Title);
        var assistant = updated.Messages.Last(m => m.Role == IntakeMessageRole.Assistant);
        Assert.Equal("Proposed \"ASB transport\" — review the draft and accept when ready.", assistant.Content);
        Assert.Equal("ASB transport", assistant.ProposedEpicTitle);
    }

    [Fact]
    public async Task SendUserMessage_EmptyReply_PersistsRetryFallback_NotCrash()
    {
        var client = new TurnScriptedClient().Turn(); // model returned nothing at all
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var updated = await agent.SendUserMessageAsync(session.Id, "hi", default);

        var assistant = updated.Messages.Last(m => m.Role == IntakeMessageRole.Assistant);
        Assert.Equal("(The model returned an empty reply — please retry.)", assistant.Content);
    }

    [Fact]
    public async Task ExactRevisionApproval_CreatesEpicIdempotently()
    {
        var client=new TurnScriptedClient().Turn(FunctionCall("create_epic","call1",("title","scope"),("description","body"),("priority",1))).Turn(Text("draft"));
        var agent=NewAgent(client);var session=await agent.StartSessionAsync("t");
        await agent.SendUserMessageAsync(session.Id,"propose");
        var draft=await Latest(); Assert.Empty(await _issues.ListAsync(new IssueFilter()));
        var first=await Approval().ApproveAsync("proj",draft.Id,draft.Revision,draft.Hash);
        var again=await Approval().ApproveAsync("proj",draft.Id,draft.Revision,draft.Hash);
        Assert.Equal(first,again);Assert.Single(await _issues.ListAsync(new IssueFilter()));
        Assert.Equal("body",(await _issues.GetAsync(first.EpicId))!.Description);
    }

    [Fact]
    public async Task SameTitleDoesNotSilentlyBindIndependentProposals()
    {
        var client=new TurnScriptedClient().Turn(
            FunctionCall("create_epic","call1",("title","same"),("description","one")),
            FunctionCall("create_epic","call2",("title","same"),("description","two"))).Turn(Text("drafts"));
        var agent=NewAgent(client);var session=await agent.StartSessionAsync("t");
        await agent.SendUserMessageAsync(session.Id,"discuss");
        var proposals=await Proposals.ListAsync("proj"); Assert.Equal(2,proposals.Count);
        Assert.All(proposals,p=>Assert.Null(p.ApprovedAt)); Assert.Empty(await _issues.ListAsync(new IssueFilter()));
    }

    [Fact]
    public async Task DistinctTitlesCreateIndependentDurableDrafts()
    {
        var client=new TurnScriptedClient().Turn(
            FunctionCall("create_epic","call1",("title","parent"),("description","one")),
            FunctionCall("create_epic","call2",("title","child"),("description","two"))).Turn(Text("drafts"));
        var agent=NewAgent(client);var session=await agent.StartSessionAsync("t");
        await agent.SendUserMessageAsync(session.Id,"discuss");
        var proposals=await Proposals.ListAsync("proj"); Assert.Equal(2,proposals.Count);
        Assert.Contains(proposals,p=>p.Title=="parent");Assert.Contains(proposals,p=>p.Title=="child");
        Assert.Empty(await _issues.ListAsync(new IssueFilter()));
    }

    [Fact]
    public async Task ExplicitStableIdRevisionSupersedesDraftAndRejectsOldApproval()
    {
        var session=await _intake.CreateAsync("proj","discussion");
        var initial=await Proposals.CreateAsync(new("proj","source","user","initial","old body",SessionId:session.Id));
        var client=new TurnScriptedClient().Turn(FunctionCall("create_epic","call1",("title","renamed"),("description","new body"),
            ("proposalId",initial.Id),("expectedRevision",initial.Revision))).Turn(Text("revised"));
        await NewAgent(client).SendUserMessageAsync(session.Id,"change scope");
        var current=(await Proposals.GetAsync("proj",initial.Id))!;
        Assert.Equal(2,current.Revision);Assert.Equal("new body",current.Body);
        Assert.Single(await Proposals.ListAsync("proj"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Approval().ApproveAsync("proj",initial.Id,initial.Revision,initial.Hash));
        Assert.Empty(await _issues.ListAsync(new IssueFilter()));
        await Approval().ApproveAsync("proj",current.Id,current.Revision,current.Hash);
        Assert.Single(await _issues.ListAsync(new IssueFilter()));
    }

    [Fact]
    public async Task ModelCannotReviseAnApprovedProposal()
    {
        var session=await _intake.CreateAsync("proj","discussion");
        var initial=await Proposals.CreateAsync(new("proj","source","user","scope","approved body",SessionId:session.Id));
        await Approval().ApproveAsync("proj",initial.Id,initial.Revision,initial.Hash);
        var client=new TurnScriptedClient().Turn(FunctionCall("create_epic","call1",("title","scope"),("description","changed"),
            ("proposalId",initial.Id),("expectedRevision",initial.Revision))).Turn(Text("unable"));
        await NewAgent(client).SendUserMessageAsync(session.Id,"change");
        var current=(await Proposals.GetAsync("proj",initial.Id))!;
        Assert.Equal(initial.Revision,current.Revision);Assert.Equal("approved body",current.Body);
    }

    private static AIContent FunctionCall(string name, string callId, params (string Key, object Value)[] args)
        => new FunctionCallContent(callId, name, args.ToDictionary(a => a.Key, a => (object?)a.Value));

    private static AIContent Text(string text) => new TextContent(text);

    /// <summary>One queued turn per model round-trip; each turn yields
    /// its content items as a single streaming update.</summary>
    private sealed class TurnScriptedClient : IChatClient
    {
        private readonly Queue<AIContent[]> _turns = new();

        public TurnScriptedClient Turn(params AIContent[] contents)
        {
            _turns.Enqueue(contents);
            return this;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("streaming only");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_turns.TryDequeue(out var contents) && contents.Length > 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant, contents);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
                                ["header"] = "Transport scope",
                                ["question"] = "Which scope?",
                                ["options"] = System.Text.Json.JsonSerializer.SerializeToElement(
                                    new[] { "Transport only", "Transport + outbox" }),
                            }),
                        new FunctionCallContent("call_2", "ask_question",
                            new Dictionary<string, object?>
                            {
                                ["header"] = "Extras",
                                ["question"] = "Anything else to pin down?",
                                ["multiple"] = true,
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
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => _client;
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
