using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

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
        _dbPath = TempRoot.Instance.NewDbPath("intake-agent");
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
    public async Task SendUserMessage_InjectsRepoBrief_IntoInstructions()
    {
        // 2026-08-09: the talaria intake asked the operator what the
        // tech stack was. The instructions must carry the repo brief
        // so intake asks about intent, not codebase facts.
        var repoRoot = Path.Combine(Path.GetTempPath(), "intake-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, "Talaria.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "# Talaria\n\nMessaging primitives.\n");
        try
        {
            var capturing = new CapturingChatClient(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "noted")));
            var agent = new IntakeAgent("talaria", _intake, _issues, _sprints,
                new ScriptingFactory(capturing),
                new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
                new RoleAgentRegistry(),
                _events,
                NullLogger<IntakeAgent>.Instance,
                projectRootLookup: _ => repoRoot);

            var session = await agent.StartSessionAsync("grounding", default);
            await agent.SendUserMessageAsync(session.Id, "I want a new transport", default);

            var system = capturing.InstructionsSeen
                .Concat(capturing.Seen
                    .SelectMany(m => m.Contents.OfType<TextContent>())
                    .Select(t => t.Text))
                .FirstOrDefault(t => t is not null && t.Contains("Project brief"));
            Assert.NotNull(system);
            Assert.Contains(".NET / C#", system);
            Assert.Contains("Talaria.slnx", system);
            Assert.Contains("Do NOT ask the operator about facts the project brief", system);
        }
        finally
        {
            try { Directory.Delete(repoRoot, recursive: true); } catch { }
        }
    }

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
    public async Task SendUserMessage_AgentCallsCreateEpic_WritesDraftAndPersistsLink()
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

        // The run produced: user, system (draft proposal), assistant.
        // The assistant message carries the proposal TITLE — the draft
        // has no epic id (operator rule 2026-08-14: no issue row until
        // the operator accepts).
        var assistantMsg = updated.Messages.First(m => m.Role == IntakeMessageRole.Assistant);
        Assert.Null(assistantMsg.ProposedEpicId);
        Assert.Equal("Refactor the auth flow", assistantMsg.ProposedEpicTitle);

        // The draft carries the full payload; the issue store is EMPTY.
        var draft = updated.Messages.Single(m => m.ProposedEpicDescription is not null);
        Assert.Equal("Refactor the auth flow", draft.ProposedEpicTitle);
        Assert.Equal("Migrate to the new claims API and remove the legacy session table.", draft.ProposedEpicDescription);
        Assert.Equal(3, draft.ProposedEpicPriority);
        Assert.Empty(await _issues.ListAsync(new IssueFilter { Assignee = "intake" }, default));

        // Accepting the draft creates the issue from the payload.
        var issue = await agent.AcceptProposedEpicAsync(session.Id, draft.Id, default);
        Assert.Equal("epic", issue.Type);
        Assert.Equal("Refactor the auth flow", issue.Title);
        Assert.Equal("intake", issue.Assignee);
        Assert.Equal(3, issue.Priority);

        // The dashboard event log shows proposed-accept lifecycle.
        var snapshot = _events.GetHistorySnapshot();
        Assert.Contains(snapshot, e => e.Kind == "intake.run.started");
        Assert.Contains(snapshot, e => e.Kind == "intake.epic.proposed");
        Assert.Contains(snapshot, e => e.Kind == "intake.epic.accepted");
        Assert.Contains(snapshot, e => e.Kind == "intake.run.completed");
    }

    [Fact]
    public async Task AcceptProposedEpic_CrossStoreIdCollision_StillCreatesSpec()
    {
        // 2026-08-09 live bug: issue ids are per-store sequences, so
        // talaria's epic-2 collided with porthorizon's epic-2. The
        // accept path's dedupe probe fanned out across ALL stores by
        // parent_issue_id, matched porthorizon's spec, and SKIPPED
        // creating talaria's spec — the project never entered the
        // pipeline. The probe must be scoped to the session's project.
        var otherDb = TempRoot.Instance.NewDbPath("intake-other");
        var otherIssues = new IssueStore(otherDb);
        try
        {
            var otherEpic = await otherIssues.CreateAsync(new NewIssue(Type: "epic", Title: "other project epic"));
            var otherSpecs = new SpecStore(otherIssues);
            await otherSpecs.CreateAsync(new NewSpec(
                ProjectId: "porthorizon", Title: "other project spec",
                Body: "body", Author: "test", ParentIssueId: otherEpic.Id));

            var mySpecs = new SpecStore(_issues);
            var routing = new ProjectRoutingSpecStore(
                mySpecs,
                findByProject: pid => pid == "talaria" ? mySpecs : pid == "porthorizon" ? otherSpecs : null,
                allProjectStores: () => new ISpecStore[] { mySpecs, otherSpecs });

            // The accepted epic's id must equal the other store's spec
            // parent for the collision to bite: both stores mint
            // epic-1 as their first row.
            Assert.Equal("epic-1", otherEpic.Id);
            var scripted = new ToolCallingChatClient(
                functionCalls: new[]
                {
                    new FunctionCallContent("call_1", "create_epic",
                        new Dictionary<string, object?>
                        {
                            ["title"] = "talaria launch hardening",
                            ["description"] = "scrub + license",
                            ["priority"] = 2,
                        }),
                },
                followUpText: "Proposed.");
            var agent = new IntakeAgent("talaria", _intake, _issues, _sprints,
                new ScriptingFactory(scripted),
                new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
                new RoleAgentRegistry(), _events, NullLogger<IntakeAgent>.Instance,
                specs: routing);

            var session = await agent.StartSessionAsync("launch", default);
            var updated = await agent.SendUserMessageAsync(session.Id, "harden the repo", default);
            var draft = updated.Messages.Single(m => m.ProposedEpicDescription is not null);

            // Accept creates THIS store's first issue row (epic-1) —
            // the same id as the other store's spec parent, which is
            // exactly the collision the scoped probe must survive.
            var acceptedEpic = await agent.AcceptProposedEpicAsync(session.Id, draft.Id, default);
            Assert.Equal("epic-1", acceptedEpic.Id);

            var mine = await mySpecs.ListAsync(null, null, default);
            Assert.Contains(mine, s => s.ParentIssueId == "epic-1" && s.ProjectId == "talaria");

            // The accept event carries the project so the refinement
            // queue can scope its own lookup the same way.
            var accepted = _events.GetHistorySnapshot()
                .Last(e => e.Kind == "intake.epic.accepted");
            Assert.Equal("talaria", accepted.Data?["projectId"]?.ToString());
        }
        finally
        {
            try { otherIssues.Dispose(); } catch { }
            try { File.Delete(otherDb); } catch { }
        }
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
        var draft = updated.Messages.Single(m => m.ProposedEpicDescription is not null);

        // Accept — creates the epic row and binds it to the sprint.
        var accepted = await agent.AcceptProposedEpicAsync(session.Id, draft.Id, default);
        Assert.Equal("Add a settings tab", accepted.Title);

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
        var draft = updated.Messages.Single(m => m.ProposedEpicDescription is not null);

        var accepted = await agent.AcceptProposedEpicAsync(session.Id, draft.Id, default);
        Assert.Equal("Doc sweep", accepted.Title);

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
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => _client;
    }

    /// <summary>
    /// Returns a function-call response on the first call, then a plain
    /// text response on the second. This is enough to drive one
    /// AIFunction invocation per <c>RunAsync</c>.
    /// </summary>
    private sealed class CapturingChatClient : IChatClient
    {
        private readonly ChatResponse _response;
        public List<ChatMessage> Seen { get; } = new();
        public List<string?> InstructionsSeen { get; } = new();
        public CapturingChatClient(ChatResponse response) { _response = response; }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            InstructionsSeen.Add(options?.Instructions);
            return Task.FromResult(_response);
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // The intake agent streams now (2026-08-12): the double
            // streams the same content the buffered path returns,
            // like every real provider.
            await Task.Yield();
            Seen.AddRange(messages);
            InstructionsSeen.Add(options?.Instructions);
            foreach (var msg in _response.Messages)
                yield return new ChatResponseUpdate(msg.Role, msg.Contents.ToList());
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

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
            // Stream the same sequence the buffered path returns:
            // function call first, follow-up text after the
            // middleware feeds the result back.
            await Task.Yield();
            if (_callIndex == 0 && _functionCalls.Length > 0)
            {
                _callIndex++;
                var call = _functionCalls[0];
                yield return new ChatResponseUpdate(ChatRole.Assistant, new[] { (AIContent)call });
                yield break;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, _followUpText);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
