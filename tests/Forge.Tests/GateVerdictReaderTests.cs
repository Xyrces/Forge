using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class GateVerdictReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _store;
    private readonly GateVerdictReader _reader;

    public GateVerdictReaderTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("gvr");
        _store = new IssueStore(_dbPath);
        _reader = new GateVerdictReader(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static Dictionary<string, object> PlanGateMeta(string json)
        => new() { ["planGate"] = json };

    [Fact]
    public async Task ReadsVerdicts_FromPlanGateMetadata()
    {
        // Create two tasks with planGate verdicts.
        await _store.CreateAsync(new NewIssue(
            Type: "task", Title: "first",
            Metadata: PlanGateMeta("""{"approved":false,"revisions":1,"failed":false,"verdicts":[{"gate":"plan-schema","outcome":"Approve","feedback":"schema ok"},{"gate":"plan-llm-review","outcome":"Revise","feedback":"needs more detail"}]}""")));
        await _store.CreateAsync(new NewIssue(
            Type: "task", Title: "second",
            Metadata: PlanGateMeta("""{"approved":true,"revisions":2,"failed":false,"verdicts":[{"gate":"plan-schema","outcome":"Approve","feedback":"clean schema"}]}""")));

        var result = await _reader.ListRecentAsync(50);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, v => v.TaskId == "task-1" && v.Gate == "plan-schema" && v.Outcome == "Approve" && v.Feedback == "schema ok");
        Assert.Contains(result, v => v.TaskId == "task-1" && v.Gate == "plan-llm-review" && v.Outcome == "Revise" && v.Feedback == "needs more detail");
        Assert.Contains(result, v => v.TaskId == "task-2" && v.Gate == "plan-schema" && v.Outcome == "Approve" && v.Feedback == "clean schema");

        // Every record should have a non-default timestamp
        foreach (var v in result)
            Assert.NotEqual(default, v.Timestamp);
    }

    [Fact]
    public async Task Empty_WhenNoPlanGateMetadata()
    {
        await _store.CreateAsync(new NewIssue(Type: "task", Title: "no gates"));
        await _store.CreateAsync(new NewIssue(Type: "task", Title: "plain task"));

        var result = await _reader.ListRecentAsync(50);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SkipsMalformedRows_Gracefully()
    {
        // Create one valid and one malformed planGate row.
        await _store.CreateAsync(new NewIssue(
            Type: "task", Title: "valid",
            Metadata: PlanGateMeta("""{"approved":true,"verdicts":[{"gate":"plan-schema","outcome":"Approve","feedback":"ok"}]}""")));
        await _store.CreateAsync(new NewIssue(
            Type: "task", Title: "malformed",
            Metadata: PlanGateMeta("""{this is not valid json}""")));

        var result = await _reader.ListRecentAsync(50);

        Assert.Single(result);
        Assert.Equal("task-1", result[0].TaskId);
        Assert.Equal("plan-schema", result[0].Gate);
        Assert.Equal("Approve", result[0].Outcome);
    }

    [Fact]
    public async Task Limit_Respected()
    {
        // Create 4 tasks each with 1 verdict. We'll then assert limit=2
        // returns exactly 2 verdicts (the two most recent, which with
        // equal timestamps will be earliest-created-first from ListAsync).
        for (int i = 0; i < 4; i++)
        {
            await _store.CreateAsync(new NewIssue(
                Type: "task", Title: $"task-{i}",
                Metadata: PlanGateMeta($$"""{"approved":true,"verdicts":[{"gate":"plan-schema","outcome":"Approve","feedback":"item {{i}}"}]}""")));
        }

        var all = await _reader.ListRecentAsync(50);
        Assert.Equal(4, all.Count);

        var limited = await _reader.ListRecentAsync(2);
        Assert.Equal(2, limited.Count);

        // The sort is by UpdatedAt DESC. With equal timestamps,
        // the sort is stable so task-4 (last created) sorts last
        // but after the DESC reverses that, task-4 should be first.
        // We just check limit works: limited has 2 items from the
        // set of all 4.
        var allIds = all.Select(v => v.TaskId).ToHashSet();
        foreach (var v in limited)
            Assert.Contains(v.TaskId, allIds);
    }

    [Fact]
    public async Task NonTaskTypes_Skipped()
    {
        // Create a pr-watch issue with planGate metadata.
        await _store.CreateAsync(new NewIssue(
            Type: "pr-watch", Title: "watch",
            Metadata: PlanGateMeta("""{"approved":true,"verdicts":[{"gate":"plan-schema","outcome":"Approve","feedback":"watch"}]}""")));
        // Create a real task with verdicts.
        await _store.CreateAsync(new NewIssue(
            Type: "task", Title: "real",
            Metadata: PlanGateMeta("""{"approved":true,"verdicts":[{"gate":"plan-schema","outcome":"Approve","feedback":"real task"}]}""")));

        var result = await _reader.ListRecentAsync(50);

        Assert.Single(result);
        Assert.Equal("task-1", result[0].TaskId);
    }
}
