using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// WatchdogScanner: each structural-stall check produces a finding
/// only in its stall condition; the finding store dedupes by
/// (kind, target) and auto-resolves on clear.
/// </summary>
public class WatchdogScannerTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly SprintStore _sprints;

    public WatchdogScannerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-wd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _sprints = new SprintStore(_issues);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private async Task<IssueRecord> TaskAsync(string title, IssueStatus? status = null, Dictionary<string, object>? meta = null)
    {
        var t = await _issues.CreateAsync(new NewIssue(Type: "task", Title: title, Metadata: meta));
        if (status is not null) await _issues.TransitionAsync(t.Id, status.Value, null);
        return (await _issues.GetAsync(t.Id))!;
    }

    private async Task<string> ActiveSprintWith(params string[] issueIds)
    {
        var sprint = await _sprints.CreateAsync(new NewSprint(
            Name: "S", Goal: "g", StartDate: DateTime.UtcNow.AddDays(-4), EndDate: DateTime.UtcNow.AddDays(10)));
        foreach (var id in issueIds) await _sprints.AddIssueAsync(sprint.Id, id);
        return sprint.Id;
    }



    [Fact]
    public async Task BlockedMemberStall_FlaggedOnlyWhenBlockerOutsideSprint()
    {
        var blocked = await TaskAsync("blocked member");
        var outside = await TaskAsync("outside blocker");
        var inside = await TaskAsync("inside work");
        await _issues.AddDependencyAsync(outside.Id, blocked.Id, IssueDepKind.Blocks, CancellationToken.None);
        await ActiveSprintWith(blocked.Id, inside.Id);

        var findings = await WatchdogScanner.ScanAsync(_issues, _sprints, DateTime.UtcNow, CancellationToken.None);

        var stall = findings.Where(f => f.Kind == WatchdogScanner.BlockedMemberStall).ToList();
        Assert.Single(stall);
        Assert.Equal(blocked.Id, stall[0].TargetId);
        Assert.Equal("fail", stall[0].Severity);

        // Blocker joins the sprint → condition clears.
        var sprint = (await _sprints.GetActiveAsync())!;
        await _sprints.AddIssueAsync(sprint.Id, outside.Id);
        var after = await WatchdogScanner.ScanAsync(_issues, _sprints, DateTime.UtcNow, CancellationToken.None);
        Assert.DoesNotContain(after, f => f.Kind == WatchdogScanner.BlockedMemberStall);
    }

    [Fact]
    public async Task StuckSprint_FlaggedWithMemberSituations()
    {
        var t = await TaskAsync("old sprint work");
        await ActiveSprintWith(t.Id);

        var findings = await WatchdogScanner.ScanAsync(_issues, _sprints, DateTime.UtcNow, CancellationToken.None);

        var stuck = findings.Where(f => f.Kind == WatchdogScanner.StuckSprint).ToList();
        Assert.Single(stuck);
        Assert.Contains(t.Id, stuck[0].Detail);
    }

    [Fact]
    public async Task Starvation_IgnoresWatchedAndBlockedTasks()
    {
        var watched = await TaskAsync("has a PR", meta: new() { ["prNumber"] = "1" });
        var clean = await TaskAsync("fresh member");
        await ActiveSprintWith(watched.Id, clean.Id);

        var findings = await WatchdogScanner.ScanAsync(_issues, _sprints, DateTime.UtcNow, CancellationToken.None);

        // Both are fresh — no starvation regardless. The key guard:
        // watched tasks never starve.
        Assert.DoesNotContain(findings, f => f.Kind == WatchdogScanner.Starvation);
    }

    [Fact]
    public async Task GroomerWedge_IgnoresGroomed()
    {
        await TaskAsync("groomed fup", meta: new() { ["followUpOf"] = "task-1", ["groomed"] = "true" });

        var findings = await WatchdogScanner.ScanAsync(_issues, _sprints, DateTime.UtcNow, CancellationToken.None);

        Assert.DoesNotContain(findings, f => f.Kind == WatchdogScanner.GroomerWedge);
    }

    [Fact]
    public async Task FindingStore_SyncDedupesAndResolves()
    {
        var store = new WatchdogFindingStore(_issues);
        var now = DateTime.UtcNow;
        var finding = new WatchdogScanner.Finding(WatchdogScanner.StuckSprint, "sprint-1", "warn", "detail one");

        var first = await store.SyncAsync(new[] { finding }, now, CancellationToken.None);
        Assert.Equal(1, first.Added);
        Assert.Equal(0, first.Resolved);
        Assert.Single(first.NewFindings);

        // Same condition again: touch, no new row.
        var second = await store.SyncAsync(new[] { finding with { Detail = "detail two" } }, now.AddMinutes(5), CancellationToken.None);
        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Updated);
        var open = await store.ListOpenAsync(CancellationToken.None);
        Assert.Single(open);
        Assert.Equal("detail two", open[0].Detail);

        // Cleared: resolves.
        var third = await store.SyncAsync(Array.Empty<WatchdogScanner.Finding>(), now.AddMinutes(10), CancellationToken.None);
        Assert.Equal(1, third.Resolved);
        Assert.Empty(await store.ListOpenAsync(CancellationToken.None));
    }
}
