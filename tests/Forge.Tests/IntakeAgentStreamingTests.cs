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
        var draft = updated.Messages.Single(m => m.ProposedEpicDescription is not null);
        Assert.Equal("ASB transport", draft.ProposedEpicTitle);
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
    public async Task Accept_DraftProposal_CreatesEpicRow_Idempotent()
    {
        // Operator rule 2026-08-14: the epic row + board presence are
        // created AT ACCEPT, from the draft payload — not at proposal.
        var client = new TurnScriptedClient()
            .Turn(FunctionCall("create_epic", "call_1",
                ("title", "ASB transport"), ("description", "first draft"), ("priority", 1)))
            .Turn(Text("proposed"));
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var proposed = await agent.SendUserMessageAsync(session.Id, "add asb", default);
        Assert.Empty(await _issues.ListAsync(new IssueFilter { Assignee = "intake" }, default));

        var draftMsg = proposed.Messages.Single(m => m.ProposedEpicDescription is not null);
        var issue = await agent.AcceptProposedEpicAsync(session.Id, draftMsg.Id, default);

        Assert.Equal("epic", issue.Type);
        Assert.Equal("ASB transport", issue.Title);
        Assert.Equal("first draft", issue.Description);
        Assert.Equal(1, issue.Priority);

        // The accept message carries the epic id + the source proposal
        // reference (the idempotence key); a second accept returns the
        // same epic instead of creating a duplicate.
        var after = await agent.GetSessionAsync(session.Id, default);
        Assert.Contains(after!.Messages, m =>
            m.Role == IntakeMessageRole.System
            && m.ProposedEpicId == issue.Id
            && m.Content.Contains($"Operator accepted epic {issue.Id}:")
            && m.Content.Contains($"(proposal message #{draftMsg.Id})"));
        var again = await agent.AcceptProposedEpicAsync(session.Id, draftMsg.Id, default);
        Assert.Equal(issue.Id, again.Id);
        Assert.Single(await _issues.ListAsync(new IssueFilter { Assignee = "intake" }, default));
    }

    [Fact]
    public async Task SendUserMessage_NonConsecutiveDuplicateTitle_RefinesMatchingProposal()
    {
        // Live incident 2026-08-14: after creating children 3-5 the
        // model re-fired child 2's create_epic; the latest-proposal
        // check missed it and a duplicate epic row landed. The match
        // scans every unaccepted draft by title and supersedes it.
        var client = new TurnScriptedClient()
            .Turn(FunctionCall("create_epic", "call_1",
                ("title", "P1-1: Fix the harness"), ("description", "child 1"), ("priority", 1)),
                  FunctionCall("create_epic", "call_2",
                ("title", "P1-2: Guard the consumer"), ("description", "child 2"), ("priority", 1)),
                  FunctionCall("create_epic", "call_3",
                ("title", "P1-1: Fix the harness"), ("description", "child 1 refined"), ("priority", 1)))
            .Turn(Text("done"));
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var updated = await agent.SendUserMessageAsync(session.Id, "go", default);

        Assert.Empty(await _issues.ListAsync(new IssueFilter { Assignee = "intake" }, default));
        var drafts = updated.Messages.Where(m => m.ProposedEpicDescription is not null).ToList();
        Assert.Equal(3, drafts.Count);
        var p11 = drafts.Where(d => d.ProposedEpicTitle == "P1-1: Fix the harness").ToList();
        Assert.Equal(2, p11.Count); // original + superseding refinement
        Assert.Equal("child 1 refined", p11.MaxBy(d => d.Id)!.ProposedEpicDescription);
    }

    [Fact]
    public async Task SendUserMessage_DistinctTitles_OneTurn_CreatesSeparateDrafts()
    {
        // Live incident 2026-08-14: a parent + children turn collapsed
        // into ONE epic — the refine-in-place guard (added for the
        // identical-titles duplicate storm) rewrote epic-8 four times
        // and the five children were never created. Refinement is
        // title-scoped: clearly different titles are new drafts.
        var client = new TurnScriptedClient()
            .Turn(FunctionCall("create_epic", "call_1",
                ("title", "Transport reliability bundle"), ("description", "parent"), ("priority", 2)),
                  FunctionCall("create_epic", "call_2",
                ("title", "P1-1: Fix the contract test harness"), ("description", "child 1"), ("priority", 1)),
                  FunctionCall("create_epic", "call_3",
                ("title", "P1-2: Guard ConsumeAsync re-enumeration"), ("description", "child 2"), ("priority", 1)))
            .Turn(Text("created the parent and both children"));
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var updated = await agent.SendUserMessageAsync(session.Id, "go ahead", default);

        Assert.Empty(await _issues.ListAsync(new IssueFilter { Assignee = "intake" }, default));
        var drafts = updated.Messages.Where(m => m.ProposedEpicDescription is not null).ToList();
        Assert.Equal(3, drafts.Count);
        Assert.Contains(drafts, d => d.ProposedEpicTitle == "Transport reliability bundle");
        Assert.Contains(drafts, d => d.ProposedEpicTitle == "P1-1: Fix the contract test harness");
        Assert.Contains(drafts, d => d.ProposedEpicTitle == "P1-2: Guard ConsumeAsync re-enumeration");
    }

    [Fact]
    public async Task SendUserMessage_SecondCreateEpic_RefinesExistingProposal_InsteadOfDuplicating()
    {
        // Live incident 2026-08-12: one turn fired create_epic 3× and
        // three identical epics (epic-5/6/7) landed in the backlog.
        // Same-title re-calls supersede the draft; nothing hits the
        // issue store before accept.
        var client = new TurnScriptedClient()
            .Turn(FunctionCall("create_epic", "call_1",
                ("title", "ASB transport"), ("description", "first draft"), ("priority", 2)),
                  FunctionCall("create_epic", "call_2",
                ("title", "ASB transport (refined)"), ("description", "refined draft"), ("priority", 2)))
            .Turn(Text("proposed"));
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var updated = await agent.SendUserMessageAsync(session.Id, "add asb", default);

        Assert.Empty(await _issues.ListAsync(new IssueFilter { Assignee = "intake" }, default));
        var drafts = updated.Messages.Where(m => m.ProposedEpicDescription is not null).ToList();
        Assert.Equal(2, drafts.Count); // original + superseding refinement
        var latest = drafts.MaxBy(d => d.Id)!;
        Assert.Equal("ASB transport (refined)", latest.ProposedEpicTitle);
        Assert.Equal("refined draft", latest.ProposedEpicDescription);
        Assert.Contains(updated.Messages, m =>
            m.Role == IntakeMessageRole.System && m.Content == "Updated epic proposal: ASB transport (refined)");
        var assistant = updated.Messages.Last(m => m.Role == IntakeMessageRole.Assistant);
        Assert.Equal("ASB transport (refined)", assistant.ProposedEpicTitle);

        // Accepting the latest draft creates ONE epic with the refined content.
        var issue = await agent.AcceptProposedEpicAsync(session.Id, latest.Id, default);
        Assert.Equal("ASB transport (refined)", issue.Title);
        Assert.Equal("refined draft", issue.Description);
    }

    [Fact]
    public async Task SendUserMessage_CreateEpicAfterAccept_StartsNewProposalSlot()
    {
        var client = new TurnScriptedClient()
            .Turn(FunctionCall("create_epic", "call_1",
                ("title", "First epic"), ("description", "d1"), ("priority", 2)))
            .Turn(Text("proposed"))
            .Turn(FunctionCall("create_epic", "call_2",
                ("title", "Second epic"), ("description", "d2"), ("priority", 2)))
            .Turn(Text("proposed again"));
        var agent = NewAgent(client);
        var session = await agent.StartSessionAsync("t", default);

        var first = await agent.SendUserMessageAsync(session.Id, "first", default);
        var firstDraft = first.Messages.Single(m => m.ProposedEpicDescription is not null);
        var firstEpic = await agent.AcceptProposedEpicAsync(session.Id, firstDraft.Id, default);

        var second = await agent.SendUserMessageAsync(session.Id, "second", default);
        var secondDraft = second.Messages
            .Where(m => m.ProposedEpicDescription is not null)
            .MaxBy(m => m.Id)!;
        var secondEpic = await agent.AcceptProposedEpicAsync(session.Id, secondDraft.Id, default);

        var epics = (await _issues.ListAsync(new IssueFilter { Assignee = "intake" }, default)).ToList();
        Assert.Equal(2, epics.Count);
        Assert.NotEqual(firstEpic.Id, secondEpic.Id);
        Assert.Equal("First epic", firstEpic.Title);
        Assert.Equal("Second epic", secondEpic.Title);
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
