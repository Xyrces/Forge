using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// MafAgentRunner pause/resume: a run persists its MAF session under
/// session/&lt;project|_&gt;/&lt;task&gt;/&lt;role&gt; in the memory
/// store (success AND failure); the next run of the same task+role
/// resumes it (resumed_session on the run row, prior history visible
/// to the model). Corrupt stored sessions degrade to a fresh run.
/// </summary>
public class MafAgentRunnerSessionTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _schema;
    private readonly MemoryStore _memory;
    private readonly AgentRunStore _runs;

    public MafAgentRunnerSessionTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-mss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _schema = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _memory = new MemoryStore(Path.Combine(_workDir, "issues.db"));
        _runs = new AgentRunStore(Path.Combine(_workDir, "issues.db"));
    }

    public void Dispose()
    {
        _schema.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private MafAgentRunner NewRunner(IChatClient client) => new(
        chatClientFactory: new SingleFactory(client),
        config: new LlmConfig(new ProviderConfig("stub", "", null, null, "stub-model")),
        roles: new RoleAgentRegistry(),
        logger: NullLogger<MafAgentRunner>.Instance,
        skills: null,
        rolePromptsRoot: _workDir,
        memory: _memory,
        runs: _runs);

    private static Dictionary<string, object> ContextFor(string taskId) =>
        new() { ["issueId"] = taskId };

    [Fact]
    public async Task Run_PersistsSession_SecondRunResumes()
    {
        var client = new EchoClient();
        var runner = NewRunner(client);
        var context = ContextFor("task-77");

        var first = await runner.RunAsync(AgentType.CoreDev, "first prompt", sessionId: null, context, CancellationToken.None);

        // The session blob lives in the memory store under the
        // (project, task, role) key; the run result carries the KEY,
        // never the blob.
        Assert.Equal("session/_/task-77/CoreDev", first.SessionId);
        var stored = await _memory.RecallAsync("session/_/task-77/CoreDev");
        Assert.Single(stored);
        Assert.Contains("first prompt", stored[0].Body);

        var coldRun = (await _runs.ListRecentAsync(taskId: "task-77")).Single();
        Assert.Equal(false, coldRun.ResumedSession);

        var second = await runner.RunAsync(AgentType.CoreDev, "second prompt", sessionId: null, context, CancellationToken.None);

        Assert.Equal("session/_/task-77/CoreDev", second.SessionId);
        // The resumed run's model call saw the FIRST run's
        // conversation in its incoming history.
        Assert.True(client.SawText("first prompt"), "resumed run should receive prior history");
        var runs = await _runs.ListRecentAsync(taskId: "task-77");
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.ResumedSession == true);
    }

    [Fact]
    public async Task FailedRun_PersistsPartialSession()
    {
        var runner = NewRunner(new ThrowingClient());
        var context = ContextFor("task-79");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(AgentType.CoreDev, "doomed", sessionId: null, context, CancellationToken.None));

        var stored = await _memory.RecallAsync("session/_/task-79/CoreDev");
        Assert.Single(stored);
    }

    [Fact]
    public async Task CorruptStoredSession_StartsFresh_NoThrow()
    {
        await _memory.RememberAsync("session/_/task-78/CoreDev", "{ not valid session json");
        var client = new EchoClient();
        var runner = NewRunner(client);
        var context = ContextFor("task-78");

        var result = await runner.RunAsync(AgentType.CoreDev, "fresh prompt", sessionId: null, context, CancellationToken.None);

        // No throw; the run was cold (nothing valid to resume) and
        // the junk was overwritten by a valid session on completion.
        Assert.False(client.SawText("not valid session json"));
        var run = (await _runs.ListRecentAsync(taskId: "task-78")).Single();
        Assert.Equal(false, run.ResumedSession);
        Assert.Equal("session/_/task-78/CoreDev", result.SessionId);
        var stored = await _memory.RecallAsync("session/_/task-78/CoreDev");
        Assert.Contains("fresh prompt", stored[0].Body);
    }

    [Fact]
    public async Task UntaskedRun_DoesNotPersistSession()
    {
        var runner = NewRunner(new EchoClient());

        var result = await runner.RunAsync(AgentType.CoreDev, "no task", sessionId: null, context: null, CancellationToken.None);

        Assert.Null(result.SessionId);
        Assert.Empty(await _memory.RecallAsync("session/"));
    }

    [Fact]
    public async Task SessionKeys_AreScopedPerProjectTaskRole()
    {
        Assert.Equal("session/porthorizon/task-1/CoreDev",
            MafAgentRunner.SessionKey("porthorizon", "task-1", AgentType.CoreDev));
        Assert.Equal("session/_/task-1/Reviewer",
            MafAgentRunner.SessionKey(null, "task-1", AgentType.Reviewer));
        Assert.Equal("session/_/task-1/Reviewer",
            MafAgentRunner.SessionKey("  ", "task-1", AgentType.Reviewer));
        Assert.Null(MafAgentRunner.SessionKey("porthorizon", null, AgentType.CoreDev));
    }

    private sealed class SingleFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public SingleFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }

    /// <summary>Records the incoming message history of every call
    /// (a resumed session shows up as prior turns in that history)
    /// and answers with fixed text.</summary>
    private sealed class EchoClient : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _calls = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public bool SawText(string text) =>
            _calls.SelectMany(c => c).Any(m => m.Text?.Contains(text, StringComparison.Ordinal) == true);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("provider exploded");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
