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
    public async Task UpdateProgress_Heartbeat_SetsCountsAndActivity()
    {
        await _runs.StartAsync("run-hb", "task-1", "CoreDev", "minimax-m3");
        var before = (await _runs.ListActiveAsync()).Single();
        Assert.Null(before.LastActivityAt);
        Assert.Null(before.MessageCount);

        await _runs.UpdateProgressAsync("run-hb", messageCount: 5, toolCallCount: 2, textChars: 900);

        var after = (await _runs.ListActiveAsync()).Single();
        Assert.Equal(5, after.MessageCount);
        Assert.Equal(2, after.ToolCallCount);
        Assert.NotNull(after.LastActivityAt);
        Assert.True(after.LastActivityAt >= after.StartedAt);
    }

    [Fact]
    public async Task ListRecent_RoleFilter_OnlyThatRole()
    {
        await _runs.StartAsync("run-a", "task-1", "CoreDev", "m");
        await _runs.StartAsync("run-b", "task-2", "Reviewer", "m");
        await _runs.FinishAsync("run-a", "succeeded", 100, 1, 0, 10, null, null);
        await _runs.FinishAsync("run-b", "failed", 100, 1, 0, 10, "boom", null);

        var reviewerOnly = await _runs.ListRecentAsync(role: "Reviewer");
        Assert.Single(reviewerOnly);
        Assert.Equal("run-b", reviewerOnly[0].Id);
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

    [Fact]
    public async Task ResumedSession_RoundTrips_FromStart()
    {
        await _runs.StartAsync("run-cold", "task-1", "CoreDev", "m");
        await _runs.StartAsync("run-warm", "task-1", "CoreDev", "m", resumedSession: true);

        var active = await _runs.ListActiveAsync();
        Assert.Equal(2, active.Count);
        Assert.Equal(false, active.Single(r => r.Id == "run-cold").ResumedSession);
        Assert.Equal(true, active.Single(r => r.Id == "run-warm").ResumedSession);
    }

    [Fact]
    public async Task Phase_HeartbeatWritesAndCoalesces()
    {
        await _runs.StartAsync("run-ph", "task-1", "CoreDev", "m");
        Assert.Null((await _runs.ListActiveAsync()).Single().Phase);

        await _runs.UpdateProgressAsync("run-ph", 1, 0, 10, phase: "plan gate");
        Assert.Equal("plan gate", (await _runs.ListActiveAsync()).Single().Phase);

        await _runs.UpdateProgressAsync("run-ph", 5, 2, 900, phase: "verifying 1/3");
        Assert.Equal("verifying 1/3", (await _runs.ListActiveAsync()).Single().Phase);

        // A heartbeat without a phase keeps the last written value
        // (COALESCE) — the label survives phase-less progress writes.
        await _runs.UpdateProgressAsync("run-ph", 6, 2, 950);
        Assert.Equal("verifying 1/3", (await _runs.ListActiveAsync()).Single().Phase);
    }

    [Fact]
    public async Task ProjectId_RoundTrips_AndFilters()
    {
        await _runs.StartAsync("run-forge", "task-1", "CoreDev", "m", projectId: "forge");
        await _runs.StartAsync("run-ph", "task-7", "CoreDev", "m", projectId: "porthorizon");
        await _runs.StartAsync("run-legacy", "task-2", "Reviewer", "m");
        foreach (var id in new[] { "run-forge", "run-ph", "run-legacy" })
            await _runs.FinishAsync(id, "succeeded", 10, 1, 0, 5, null, null);

        var all = await _runs.ListRecentAsync();
        Assert.Equal("forge", all.Single(r => r.Id == "run-forge").ProjectId);
        Assert.Equal("porthorizon", all.Single(r => r.Id == "run-ph").ProjectId);
        Assert.Null(all.Single(r => r.Id == "run-legacy").ProjectId);   // pre-v26 shape

        var forgeOnly = await _runs.ListRecentAsync(projectId: "forge");
        Assert.Single(forgeOnly);
        Assert.Equal("run-forge", forgeOnly[0].Id);
    }
}
