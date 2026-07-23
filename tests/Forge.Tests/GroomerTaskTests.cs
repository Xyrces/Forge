using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration.TestHelpers;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// GroomerAgent.GroomTaskAsync: technical grooming for ad-hoc
/// tasks (operator rule 2026-07-23 — no task enters a sprint
/// without it). The groomer verifies against the vision and plans
/// against current state, then approves (metadata groomed=true)
/// or closes.
/// </summary>
public class GroomerTaskTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly MemoryStore _memory;
    private readonly InMemoryDashboardEventBus _events;

    public GroomerTaskTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-groomt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _specs = new SpecStore(_issues);
        // Memory table rides the IssueStore schema (Program.cs does
        // the same bootstrap for memory.db).
        var memBootstrap = new IssueStore(Path.Combine(_workDir, "memory.db"));
        memBootstrap.Dispose();
        _memory = new MemoryStore(Path.Combine(_workDir, "memory.db"));
        _events = new InMemoryDashboardEventBus();
    }

    public void Dispose()
    {
        _issues.Dispose();
        _specs.Dispose();
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private GroomerAgent NewAgent(IChatClient client) => new(
        _issues, _specs, _events,
        new SingleClientFactory(client),
        new LlmConfig(new ProviderConfig("stub", "", null, null, "stub-model")),
        NullLogger<GroomerAgent>.Instance,
        memory: _memory, projectRoot: _workDir);

    [Fact]
    public async Task Approve_MarksGroomedWithNote_StatusStaysPending()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "one-off"));
        var client = new RecordingToolCallClient(
            "approve_task", new Dictionary<string, object?> { ["note"] = "serves the vision" });

        var outcome = await NewAgent(client).GroomTaskAsync(task.Id);

        Assert.Equal("groomed", outcome);
        var after = await _issues.GetAsync(task.Id);
        Assert.Equal(IssueStatus.Pending, after!.Status);
        Assert.Equal("true", after.GetMetadata("groomed"));
        Assert.Equal("serves the vision", after.GetMetadata("groomNote"));
        Assert.NotNull(after.GetMetadata("groomRunId"));
    }

    [Fact]
    public async Task Close_TransitionsToClosed_WithReason()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "obsolete"));
        var client = new RecordingToolCallClient(
            "close_task", new Dictionary<string, object?> { ["reason"] = "already covered by task-9" });

        var outcome = await NewAgent(client).GroomTaskAsync(task.Id);

        Assert.Equal("closed", outcome);
        var after = await _issues.GetAsync(task.Id);
        Assert.Equal(IssueStatus.Closed, after!.Status);
        Assert.Equal("already covered by task-9", after.GetMetadata("groomCloseReason"));
        Assert.NotEqual("true", after.GetMetadata("groomed"));
    }

    [Fact]
    public async Task Skips_AlreadyGroomed_Chained_AndNonPending_WithoutLlm()
    {
        var groomed = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "g",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        var parent = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "p"));
        var chained = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "c", ParentId: parent.Id));
        var done = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "d"));
        await _issues.TransitionAsync(done.Id, IssueStatus.Completed, null);

        var client = new RecordingToolCallClient("approve_task", new Dictionary<string, object?> { ["note"] = "x" });
        var agent = NewAgent(client);

        Assert.Equal("skipped", await agent.GroomTaskAsync(groomed.Id));
        Assert.Equal("skipped", await agent.GroomTaskAsync(chained.Id));
        Assert.Equal("skipped", await agent.GroomTaskAsync(done.Id));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Grounding_IncludesVisionAndOpenWork_InPrompt()
    {
        await _memory.RememberAsync("vision/master", "VISION: build the self-driving loop", ttlDays: null, CancellationToken.None);
        var other = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "existing open work"));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "one-off"));
        var client = new RecordingToolCallClient(
            "approve_task", new Dictionary<string, object?> { ["note"] = "ok" });

        await NewAgent(client).GroomTaskAsync(task.Id);

        var promptText = (client.FirstOptions?.Instructions ?? "")
            + "\n" + string.Join("\n", client.FirstRequest.SelectMany(m => m.Contents).Select(c => c.ToString()));
        Assert.Contains("VISION: build the self-driving loop", promptText);
        Assert.Contains("existing open work", promptText);
    }

    [Fact]
    public async Task Grounding_ExcludesTheCandidateItself()
    {
        // Live bug (task-152): the candidate appeared in its own
        // open-work digest and the groomer closed it as a
        // "duplicate of existing work".
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "unique snowflake"));
        var client = new RecordingToolCallClient(
            "approve_task", new Dictionary<string, object?> { ["note"] = "ok" });

        await NewAgent(client).GroomTaskAsync(task.Id);

        // The digest lives in the system instructions; the candidate
        // id must not appear there as an open-work row.
        var instructions = client.FirstOptions?.Instructions ?? "";
        Assert.DoesNotContain($"- {task.Id} [", instructions);
    }

    private sealed class SingleClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public SingleClientFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }

    /// <summary>
    /// Tool-call-on-first-turn, plain-text-on-second. Records the
    /// first request's messages so tests can assert on the
    /// grounding block the groomer was given.
    /// </summary>
    private sealed class RecordingToolCallClient : IChatClient
    {
        private readonly string _toolName;
        private readonly Dictionary<string, object?> _args;
        public int CallCount;
        public IReadOnlyList<ChatMessage> FirstRequest = Array.Empty<ChatMessage>();
        public ChatOptions? FirstOptions;

        public RecordingToolCallClient(string toolName, Dictionary<string, object?> args)
        {
            _toolName = toolName;
            _args = args;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                FirstRequest = messages.ToList();
                FirstOptions = options;
                var call = new FunctionCallContent("c1", _toolName, _args);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
