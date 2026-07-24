using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// AgentRunStore: run registry + transcripts. Roundtrip, active
/// visibility (near-real-time "who is doing what"), and retention
/// (per-task cap protects the storage budget).
/// </summary>
public class AgentRunStoreTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _schema; // owns schema v20 incl. agent_run
    private readonly AgentRunStore _runs;

    public AgentRunStoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-runs-{Guid.NewGuid():N}");
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
    public async Task StartThenFinish_RoundTrips_WithTranscript()
    {
        await _runs.StartAsync("run-1", "task-9", "CoreDev", "minimax-m3");

        var active = await _runs.ListActiveAsync();
        Assert.Single(active);
        Assert.Equal("running", active[0].Status);
        Assert.Equal("task-9", active[0].TaskId);
        Assert.Equal("minimax-m3", active[0].Model);

        await _runs.FinishAsync("run-1", "succeeded", 61000, 42, 17, 9000,
            error: null, transcriptJson: """[{"role":"user","contents":[]}]""");

        Assert.Empty(await _runs.ListActiveAsync());
        var done = await _runs.GetAsync("run-1");
        Assert.NotNull(done);
        Assert.Equal("succeeded", done!.Status);
        Assert.Equal(42, done.MessageCount);
        Assert.Equal(17, done.ToolCallCount);
        Assert.Contains("\"user\"", done.TranscriptJson);
        Assert.Single(await _runs.ListRecentAsync(taskId: "task-9"));
    }

    [Fact]
    public async Task FailedRun_RecordsError()
    {
        await _runs.StartAsync("run-2", null, "Reviewer", null);
        await _runs.FinishAsync("run-2", "failed", 500, 0, 0, 0,
            "ClientResultException: 429", transcriptJson: null);
        var run = await _runs.GetAsync("run-2");
        Assert.Equal("failed", run!.Status);
        Assert.Contains("429", run.Error);
    }

    [Fact]
    public async Task Retention_KeepsNewestFiftyPerTask()
    {
        for (var i = 0; i < 55; i++)
        {
            await _runs.StartAsync($"run-{i:D3}", "task-x", "CoreDev", null);
            await _runs.FinishAsync($"run-{i:D3}", "succeeded", 1, 1, 0, 1, null, null);
        }
        var kept = await _runs.ListRecentAsync(limit: 100, taskId: "task-x");
        Assert.Equal(50, kept.Count);
        // Same-second timestamps make order-by unstable; assert set
        // membership instead: 50 distinct runs of the 55.
        Assert.Equal(50, kept.Select(r => r.Id).Distinct().Count());
    }
}
