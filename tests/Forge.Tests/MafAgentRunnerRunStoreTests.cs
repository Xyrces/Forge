using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// MafAgentRunner + AgentRunStore: a run registers as 'running'
/// at start (near-real-time "who is doing what") and finishes with
/// outcome + full transcript (text, tool calls, tool results).
/// </summary>
public class MafAgentRunnerRunStoreTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _schema;
    private readonly AgentRunStore _runs;

    [Fact]
    public void BuildTranscriptJson_IncludesThinkingContent()
    {
        // DeepSeek-style reasoning: TextReasoningContent must survive
        // serialization as a "thinking" block (the run page renders
        // it collapsible) — previously dropped entirely.
        var messages = new[]
        {
            new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                new Microsoft.Extensions.AI.AIContent[]
                {
                    new Microsoft.Extensions.AI.TextReasoningContent("let me think about this…"),
                    new Microsoft.Extensions.AI.TextContent("the answer is 4"),
                }),
        };
        var json = Agents.MafAgentRunner.BuildTranscriptJson(messages);
        Assert.Contains("\"thinking\"", json);
        Assert.Contains("let me think about this", json);
        Assert.Contains("the answer is 4", json);
    }

    public MafAgentRunnerRunStoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-mrs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _schema = new IssueStore(Path.Combine(_workDir, "issues.db"));
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
        rolePromptsRoot: _workDir,   // no agents/ here → fallback prompt (fine)
        runs: _runs);

    [Fact]
    public async Task SuccessfulRun_PersistsTranscript_WithToolCallAndResult()
    {
        var client = new ToolCallThenTextClient();
        var runner = NewRunner(client);
        var context = new Dictionary<string, object> { ["issueId"] = "task-42" };

        // Active visibility mid-run: the row exists as 'running'
        // before the first LLM response returns.
        var runTask = runner.RunAsync(AgentType.CoreDev, "do the thing", sessionId: null, context, CancellationToken.None);
        await client.FirstCallObserved.Task;
        var active = await _runs.ListActiveAsync();
        Assert.Single(active);
        Assert.Equal("task-42", active[0].TaskId);
        Assert.Equal("CoreDev", active[0].Role);

        client.Release.SetResult();
        await runTask;

        var done = (await _runs.ListRecentAsync(taskId: "task-42")).Single();
        Assert.Equal("succeeded", done.Status);
        Assert.Equal("stub-model", done.Model);
        Assert.True(done.ToolCallCount >= 1);
        var transcript = done.TranscriptJson!;
        Assert.Contains("bash", transcript);
        Assert.Contains("tool_call", transcript);
        Assert.Contains("tool_result", transcript);
        Assert.Contains("exit code 0", transcript);
        Assert.Contains("do the thing", transcript);   // user prompt captured
    }

    [Fact]
    public async Task FailedRun_RecordedWithError()
    {
        var runner = NewRunner(new ThrowingClient());
        var context = new Dictionary<string, object> { ["issueId"] = "task-43" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(AgentType.QA, "x", sessionId: null, context, CancellationToken.None));

        var run = (await _runs.ListRecentAsync(taskId: "task-43")).Single();
        Assert.Equal("failed", run.Status);
        Assert.Contains("provider exploded", run.Error);
    }

    private sealed class SingleFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public SingleFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }

    /// <summary>First turn: a bash tool call (waits so the test can
    /// observe the 'running' row). Second turn: plain text.</summary>
    private sealed class ToolCallThenTextClient : IChatClient
    {
        private int _calls;
        public readonly TaskCompletionSource FirstCallObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                FirstCallObserved.SetResult();
                await Release.Task;
                var call = new FunctionCallContent("c1", "bash", new Dictionary<string, object?> { ["command"] = "dotnet build" });
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call }));
            }
            // FunctionInvokingChatClient would normally execute the
            // tool; the stub short-circuits by returning the result
            // then final text in one go.
            return new ChatResponse(new[]
            {
                new ChatMessage(ChatRole.Tool, new[] { (AIContent)new FunctionResultContent("c1", "exit code 0\nBuild succeeded.") }),
                new ChatMessage(ChatRole.Assistant, "Build passed. Done."),
            });
        }

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
