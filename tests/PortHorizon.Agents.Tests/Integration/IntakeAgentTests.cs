using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using Xunit;

namespace PortHorizon.Agents.Tests.Integration;

/// <summary>
/// P1.4 tests: <see cref="IntakeAgent"/> end-to-end with a scripted
/// <see cref="IChatClient"/>. Exercises the create_epic AIFunction and
/// the Accept-proposed-epic flow.
/// </summary>
public class IntakeAgentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SprintStore _sprints;
    private readonly IntakeStore _intake;
    private readonly InMemoryDashboardEventBus _events;
    private readonly List<DashboardEvent> _published;

    public IntakeAgentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-intake-agent-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _sprints = new SprintStore(_issues);
        _intake = new IntakeStore(_issues);
        _events = new InMemoryDashboardEventBus();
        _published = new List<DashboardEvent>();
        // Pull events after each call (the agent publishes synchronously,
        // so a snapshot after RunAsync returns is sufficient).
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private IntakeAgent BuildAgent(IChatClient client, string projectId = "PortHorizon")
        => new(projectId, _intake, _issues, _sprints,
            new ScriptingFactory(client),
            new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
            new RoleAgentRegistry(),
            _events,
            NullLogger<IntakeAgent>.Instance);

    [Fact]
    public async Task SendUserMessage_SimpleChat_PersistsBothMessages()
    {
        // Scripted: agent returns "Hello, operator!"
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello, operator!")));
        var agent = BuildAgent(scripted);

        var session = await agent.StartSessionAsync("Hi", default);
        var updated = await agent.SendUserMessageAsync(session.Id, "hi there", default);

        Assert.Equal(2, updated.Messages.Count);
        Assert.Equal(IntakeMessageRole.User, updated.Messages[0].Role);
        Assert.Equal("hi there", updated.Messages[0].Content);
        Assert.Equal(IntakeMessageRole.Assistant, updated.Messages[1].Role);
        Assert.Equal("Hello, operator!", updated.Messages[1].Content);
        Assert.Null(updated.Messages[1].ProposedEpicId);
    }

    [Fact]
    public async Task SendUserMessage_AgentCallsCreateEpic_WritesEpicAndPersistsLink()
    {
        // Scripted: agent calls create_epic on the first turn, returns a
        // final text on the second.
        var scripted = new ToolCallingChatClient(
            functionCalls: new[]
            {
                new FunctionCallContent("call_1", "create_epic",
                    new Dictionary<string, object?>
                    {
                        ["title"] = "Refactor the auth flow",
                        ["description"] = "Migrate to the new claims API and remove the legacy session table.",
                        ["priority"] = 3,
                    }),
            },
            followUpText: "I've proposed an epic; please review and accept.");
        var agent = BuildAgent(scripted);

        var session = await agent.StartSessionAsync("Auth epic", default);
        var updated = await agent.SendUserMessageAsync(session.Id, "I want to refactor auth", default);

        // The agent run produced: user, system (proposed epic), assistant.
        // The assistant message carries the proposed epic id.
        var assistantMsg = updated.Messages.First(m => m.Role == IntakeMessageRole.Assistant);
        Assert.NotNull(assistantMsg.ProposedEpicId);
        Assert.StartsWith("epic-", assistantMsg.ProposedEpicId!);
        Assert.Equal("Refactor the auth flow", assistantMsg.ProposedEpicTitle);

        // The issue was actually created in the store.
        var issue = await _issues.GetAsync(assistantMsg.ProposedEpicId!, default);
        Assert.NotNull(issue);
        Assert.Equal("epic", issue!.Type);
        Assert.Equal("Refactor the auth flow", issue.Title);
        Assert.Equal("intake", issue.Assignee);
        Assert.Equal(3, issue.Priority);

        // The dashboard event log shows proposed-accept lifecycle.
        var snapshot = _events.GetHistorySnapshot();
        Assert.Contains(snapshot, e => e.Kind == "intake.run.started");
        Assert.Contains(snapshot, e => e.Kind == "intake.epic.proposed");
        Assert.Contains(snapshot, e => e.Kind == "intake.run.completed");
    }

    [Fact]
    public async Task AcceptProposedEpic_BindsToActiveSprint()
    {
        // Set up: active sprint + scripted tool call.
        var sprint = await _sprints.CreateAsync(new NewSprint(
            Name: "Sprint 1", Goal: "Ship the intake path",
            StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(14)), default);
        await _sprints.SetActiveAsync(sprint.Id, default);

        var scripted = new ToolCallingChatClient(
            functionCalls: new[]
            {
                new FunctionCallContent("call_1", "create_epic",
                    new Dictionary<string, object?>
                    {
                        ["title"] = "Add a settings tab",
                        ["description"] = "Operators want to edit skills from the UI.",
                        ["priority"] = 2,
                    }),
            },
            followUpText: "Proposed.");
        var agent = BuildAgent(scripted);

        var session = await agent.StartSessionAsync("Settings", default);
        var updated = await agent.SendUserMessageAsync(session.Id, "settings tab", default);
        var assistantMsg = updated.Messages.First(m => m.Role == IntakeMessageRole.Assistant);

        // Accept.
        var accepted = await agent.AcceptProposedEpicAsync(session.Id, assistantMsg.Id, default);
        Assert.Equal(assistantMsg.ProposedEpicId, accepted.Id);

        // The sprint now contains the epic.
        var ids = await _sprints.GetIssueIdsAsync(sprint.Id, default);
        Assert.Contains(accepted.Id, ids);

        // The session has a system message recording the acceptance.
        var refreshed = await _intake.GetAsync(session.Id, default);
        Assert.Contains(refreshed!.Messages, m =>
            m.Role == IntakeMessageRole.System &&
            m.Content.Contains("Operator accepted epic", StringComparison.Ordinal) &&
            m.Content.Contains(sprint.Id, StringComparison.Ordinal));

        // Dashboard event log shows the accept.
        var snapshot = _events.GetHistorySnapshot();
        Assert.Contains(snapshot, e => e.Kind == "intake.epic.accepted");
    }

    [Fact]
    public async Task AcceptProposedEpic_NoActiveSprint_StillAccepts()
    {
        // No active sprint; accept must still record the system message
        // and not crash.
        var scripted = new ToolCallingChatClient(
            functionCalls: new[]
            {
                new FunctionCallContent("call_1", "create_epic",
                    new Dictionary<string, object?>
                    {
                        ["title"] = "Doc sweep",
                        ["description"] = "Walk the docs and fix stale links.",
                        ["priority"] = 1,
                    }),
            },
            followUpText: "Proposed.");
        var agent = BuildAgent(scripted);

        var session = await agent.StartSessionAsync(null, default);
        var updated = await agent.SendUserMessageAsync(session.Id, "sweep docs", default);
        var assistantMsg = updated.Messages.First(m => m.Role == IntakeMessageRole.Assistant);

        var accepted = await agent.AcceptProposedEpicAsync(session.Id, assistantMsg.Id, default);
        Assert.Equal(assistantMsg.ProposedEpicId, accepted.Id);

        var refreshed = await _intake.GetAsync(session.Id, default);
        Assert.Contains(refreshed!.Messages, m =>
            m.Role == IntakeMessageRole.System &&
            m.Content.Contains("no active sprint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptProposedEpic_NotAProposal_Throws()
    {
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "no tool call")));
        var agent = BuildAgent(scripted);

        var session = await agent.StartSessionAsync("t", default);
        var updated = await agent.SendUserMessageAsync(session.Id, "hi", default);
        var assistantMsg = updated.Messages.First(m => m.Role == IntakeMessageRole.Assistant);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.AcceptProposedEpicAsync(session.Id, assistantMsg.Id, default));
    }

    [Fact]
    public async Task Registry_PerProjectLazy()
    {
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var registry = new IntakeAgentRegistry(projectId =>
            BuildAgentWithFactory(projectId, scripted));

        var a = registry.ForProject("P1");
        var b = registry.ForProject("P1"); // same project -> same instance
        var c = registry.ForProject("P2"); // different project -> new instance

        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.Contains("P1", a.ProjectId);
        Assert.Contains("P2", c.ProjectId);
    }

    private IntakeAgent BuildAgentWithFactory(string projectId, IChatClient client)
        => new(projectId, _intake, _issues, _sprints, new ScriptingFactory(client),
            new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
            new RoleAgentRegistry(), _events, NullLogger<IntakeAgent>.Instance);

    private sealed class ScriptingFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public ScriptingFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }

    /// <summary>
    /// Returns a function-call response on the first call, then a plain
    /// text response on the second. This is enough to drive one
    /// AIFunction invocation per <c>RunAsync</c>.
    /// </summary>
    private sealed class ToolCallingChatClient : IChatClient
    {
        private readonly FunctionCallContent[] _functionCalls;
        private readonly string _followUpText;
        private int _callIndex;
        public ToolCallingChatClient(FunctionCallContent[] functionCalls, string followUpText)
        {
            _functionCalls = functionCalls;
            _followUpText = followUpText;
        }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (_callIndex == 0 && _functionCalls.Length > 0)
            {
                _callIndex++;
                var call = _functionCalls[0];
                var msg = new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call });
                return Task.FromResult(new ChatResponse(msg));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _followUpText)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
