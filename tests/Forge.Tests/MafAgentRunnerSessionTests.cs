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
    private readonly IssueStore _issues;
    private readonly MemoryStore _memory;
    private readonly AgentRunStore _runs;

    public MafAgentRunnerSessionTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("mss");
        Directory.CreateDirectory(_workDir);
        _schema = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _memory = new MemoryStore(Path.Combine(_workDir, "issues.db"));
        _runs = new AgentRunStore(Path.Combine(_workDir, "issues.db"));
    }

    public void Dispose()
    {
        _schema.Dispose();
        try { _issues.Dispose(); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private MafAgentRunner NewRunner(IChatClient client,
        IIssueStore? issues = null, Func<string?, IIssueStore?>? issueStoreLookup = null) => new(
        chatClientFactory: new SingleFactory(client),
        config: new LlmConfig(new ProviderConfig("stub", "", null, null, "stub-model")),
        roles: new RoleAgentRegistry(),
        logger: NullLogger<MafAgentRunner>.Instance,
        skills: null,
        rolePromptsRoot: _workDir,
        memory: _memory,
        runs: _runs,
        issues: issues,
        issueStoreLookup: issueStoreLookup);

    [Fact]
    public async Task FollowUp_FilesIntoRunProjectStore()
    {
        // Operator report 2026-07-31: PH follow-ups landed in the
        // forge backlog (the tool used the runner's primary store).
        // The run's projectId must route the file_followup write.
        var phIssues = new IssueStore(Path.Combine(_workDir, "ph.db"));
        try
        {
            var client = new FollowUpClient();
            var runner = NewRunner(client,
                issues: _issues,
                issueStoreLookup: pid => pid == "porthorizon" ? phIssues : null);
            var context = ContextFor("task-90");
            context["projectId"] = "porthorizon";

            var result = await runner.RunAsync(AgentType.CoreDev, "do work", sessionId: null, context, CancellationToken.None);

            Assert.Equal("done", result.Text);
            // Post-2026-07-31 model: a plain file_followup lands as a
            // DRAFT in the run's project store (materialized at sprint
            // completion) — the routing assertion still applies.
            var drafts = await new FollowUpDraftStore(phIssues).ListUnconsumedAsync();
            var draft = Assert.Single(drafts);
            Assert.Equal("ph follow-up", draft.Title);
            Assert.Equal("task-90", draft.SourceIssueId);
            Assert.Empty(await new FollowUpDraftStore(_issues).ListUnconsumedAsync());
            Assert.Empty(await phIssues.ListAsync(new IssueFilter()));
        }
        finally
        {
            try { phIssues.Dispose(); } catch { }
        }
    }

    /// <summary>Emits one file_followup tool call, then final text.</summary>
    private sealed class FollowUpClient : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                var call = new FunctionCallContent("f1", "file_followup",
                    new Dictionary<string, object?> { ["title"] = "ph follow-up", ["description"] = "found while working" });
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
    public async Task PoisonedSession_DroppedAndRestartedCold()
    {
        // Live incident 2026-07-30 (porthorizon task-20): a session
        // persisted mid-tool-loop 400s every resume ("tool_calls must
        // be followed by tool messages") and burned two dispatch
        // cycles. The runner must detect the provider error, drop the
        // stored session, and restart cold — in the same run.
        await _memory.RememberAsync("session/_/task-80/CoreDev", "{\"poisoned\":true}", ttlDays: null);
        var client = new PoisonOnceClient();
        var runner = NewRunner(client);
        var context = ContextFor("task-80");

        var result = await runner.RunAsync(AgentType.CoreDev, "the prompt", sessionId: null, context, CancellationToken.None);

        Assert.Equal("done", result.Text);
        Assert.Equal(2, client.Calls);
        // The poison blob was deleted; what remains is the cold run's
        // freshly persisted session.
        var stored = await _memory.RecallAsync("session/_/task-80/CoreDev");
        Assert.DoesNotContain(stored, h => h.Body.Contains("poisoned"));
    }

    [Fact]
    public async Task PoisonedSession_MinimaxToolPairingPhrasing_DroppedAndRestartedCold()
    {
        // Live incident 2026-08-14 (porthorizon task-525): a session
        // persisted mid-tool-loop 400'd every resume on minimax's
        // Anthropic endpoint ("tool call result does not follow tool
        // call (2013)"); requeue replayed the same poisoned blob and
        // the task burned dispatch cycles until the operator noticed.
        await _memory.RememberAsync("session/_/task-525x/CoreDev", "{\"poisoned\":true}", ttlDays: null);
        var client = new PoisonOnceClient(
            "HTTP 400: invalid params, tool call result does not follow tool call (2013) " +
            "[uri=https://api.minimax.io/anthropic/v1/messages]");
        var runner = NewRunner(client);
        var context = ContextFor("task-525x");

        var result = await runner.RunAsync(AgentType.CoreDev, "the prompt", sessionId: null, context, CancellationToken.None);

        Assert.Equal("done", result.Text);
        Assert.Equal(2, client.Calls);
        var stored = await _memory.RecallAsync("session/_/task-525x/CoreDev");
        Assert.DoesNotContain(stored, h => h.Body.Contains("poisoned"));
    }

    [Theory]
    [InlineData("HTTP 400: an assistant message with 'tool_calls' must be followed by tool messages responding to each 'tool_call_id'.", true)]
    [InlineData("HTTP 400: Invalid request: Your request exceeded model token limit: max 262144", true)]
    [InlineData("HTTP 400: total message size 35670664 exceeds limit 33554432", true)]
    [InlineData("ClientResultException: HTTP 400 (AI_APICallError: )  invalid params, context window exceeds limit (2013)", true)]
    [InlineData("HTTP 400: invalid params, tool call result does not follow tool call (2013) [uri=https://api.minimax.io/anthropic/v1/messages]", true)]
    [InlineData("HTTP 400: messages.5: `tool_use` ids were found without `tool_result` blocks immediately after", true)]
    [InlineData("HTTP 400: unexpected `tool_use_id` found in `tool_result` block: call_123", true)]
    [InlineData("HTTP 429 Too Many Requests: rate limit reached", false)]
    [InlineData("provider exploded", false)]
    public void IsPoisonedSessionError_Classifies(string message, bool expected)
    {
        Assert.Equal(expected, MafAgentRunner.IsPoisonedSessionError(new HttpRequestException(message)));
        // Nested: the gateway wraps the upstream error.
        Assert.Equal(expected, MafAgentRunner.IsPoisonedSessionError(
            new InvalidOperationException("wrapped", new HttpRequestException(message))));
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
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => _client;
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

    [Fact]
    public async Task ReviewerBash_IsReadOnly_MutationsRefused()
    {
        // The Reviewer gets the PR worktree for evidence-gathering
        // (truncated diff pastes produced false blocks — porthorizon
        // task-17, 2026-07-30) with mutations hard-refused.
        var client = new ReviewerBashClient();
        var runner = NewRunner(client);
        var context = ContextFor("task-81");
        context["worktreePath"] = _workDir;

        var result = await runner.RunAsync(AgentType.Reviewer, "review the change", sessionId: null, context, CancellationToken.None);

        Assert.Equal("done", result.Text);
        Assert.NotNull(client.RefusalSeen);
        Assert.Contains("read-only", client.RefusalSeen, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(client.LsResultSeen);
        Assert.DoesNotContain("REFUSED", client.LsResultSeen);
    }

    /// <summary>Emits a mutating bash call, then a read-only one,
    /// capturing the tool results MAF feeds back.</summary>
    private sealed class ReviewerBashClient : IChatClient
    {
        private int _calls;
        public string? RefusalSeen;
        public string? LsResultSeen;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1) return ToolCall("c1", "git commit -m x");
            if (_calls == 2)
            {
                RefusalSeen = LastToolResult(messages);
                return ToolCall("c2", "ls");
            }
            LsResultSeen = LastToolResult(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        private static Task<ChatResponse> ToolCall(string id, string command)
        {
            var call = new FunctionCallContent(id, "bash", new Dictionary<string, object?> { ["command"] = command });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
        }

        private static string? LastToolResult(IEnumerable<ChatMessage> messages) =>
            messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault()?.Result?.ToString();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>First call 400s with the poisoned-session pattern
    /// (dangling tool_calls), subsequent calls succeed — simulates the
    /// provider rejecting a persisted mid-tool-loop session, then the
    /// cold restart going through.</summary>
    private sealed class PoisonOnceClient : IChatClient
    {
        private readonly string _error;
        public PoisonOnceClient(string? error = null) => _error = error ??
            "HTTP 400: an assistant message with 'tool_calls' must be followed by tool messages " +
            "responding to each 'tool_call_id'. The following tool_call_ids did not have response messages: call_1";
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
            {
                throw new HttpRequestException(_error);
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
