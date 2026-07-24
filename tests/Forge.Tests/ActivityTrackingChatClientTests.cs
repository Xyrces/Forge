using Microsoft.Extensions.AI;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// ActivityTrackingChatClient: per-round-trip heartbeats. MAF loops
/// model→tool→model INSIDE one agent.RunAsync — without this the run
/// row shows "no output" for the whole run (observed live 2026-07-24:
/// a run looked dead for minutes while actively working).
/// </summary>
public class ActivityTrackingChatClientTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _schema;
    private readonly AgentRunStore _runs;

    public ActivityTrackingChatClientTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-activity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _schema = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _runs = new AgentRunStore(Path.Combine(_workDir, "issues.db"));
    }

    public void Dispose()
    {
        _schema.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task EachRoundTrip_UpdatesProgressCounters()
    {
        await _runs.StartAsync("run-act", "task-1", "CoreDev", "m");
        var inner = new StubChatClient();
        var tracked = new ActivityTrackingChatClient(inner, "run-act", _runs);
        var msgs = new[] { new ChatMessage(ChatRole.User, "go") };

        await tracked.GetResponseAsync(msgs);
        var after1 = (await _runs.ListActiveAsync()).Single();
        Assert.Equal(1, after1.MessageCount);
        Assert.Equal(2, after1.ToolCallCount);   // stub response carries 2 tool calls
        Assert.True(after1.TextChars > 0);
        Assert.NotNull(after1.LastActivityAt);

        await tracked.GetResponseAsync(msgs);
        var after2 = (await _runs.ListActiveAsync()).Single();
        Assert.Equal(2, after2.MessageCount);    // round-trips accumulate
        Assert.Equal(4, after2.ToolCallCount);
    }

    [Fact]
    public async Task TrackingFailure_DoesNotBreakTheCall()
    {
        // No StartAsync row — the UPDATE hits zero rows, and a broken
        // store would throw. Either way the response must come back.
        var tracked = new ActivityTrackingChatClient(new StubChatClient(), "nonexistent", _runs);
        var resp = await tracked.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "go") });
        Assert.NotNull(resp);
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new TextContent("working on it"),
                new FunctionCallContent("call-1", "bash"),
                new FunctionCallContent("call-2", "read"),
            ]));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
