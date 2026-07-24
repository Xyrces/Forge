using Forge.Core;
using Forge.Dashboard.Now;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// NowFeed: the operator-facing derivation — attention ranking,
/// live stages, and plain-language waiting reasons.
/// </summary>
public class NowFeedTests
{
    private static IssueRecord Issue(
        string id, IssueStatus status, string? pr = null,
        string? groomed = null, string? noProg = null, string? rework = null,
        string? lastError = null, DispatchCheckpoint? ckpt = null,
        DateTime? updatedAt = null)
    {
        var meta = new Dictionary<string, object>();
        if (pr is not null) meta["prNumber"] = pr;
        if (groomed is not null) meta["groomed"] = groomed;
        if (noProg is not null) meta["noProgressAttempts"] = noProg;
        if (rework is not null) meta["reworkAttempts"] = rework;
        if (lastError is not null) meta["lastError"] = lastError;
        return new(id, id, "task", $"title {id}", null, status, 2, null,
            updatedAt ?? DateTime.UtcNow, updatedAt ?? DateTime.UtcNow, null,
            System.Text.Json.JsonSerializer.Serialize(meta), null, ckpt);
    }

    [Fact]
    public void Attention_FailedFirst_ThenBreakerRisk_ThenNoOps()
    {
        var now = DateTime.UtcNow;
        var items = NowFeed.BuildAttention(
            new[]
            {
                Issue("task-done", IssueStatus.Completed, updatedAt: now),              // unverified no-op
                Issue("task-risk", IssueStatus.Pending, noProg: "2"),                   // breaker risk
                Issue("task-dead", IssueStatus.Failed, lastError: "boom"),              // failed
            },
            new Dictionary<string, bool> { ["merge"] = true },                           // held gate
            now);

        // fail group precedes warn/info; both fail kinds present.
        var firstNonFail = items.FindIndex(i => i.Severity != "fail");
        Assert.True(firstNonFail > 0);
        Assert.Contains(items, i => i.Kind == "failed-task" && i.Severity == "fail");
        Assert.Contains(items, i => i.Kind == "breaker-risk" && i.Severity == "fail");
        Assert.Contains(items, i => i.Kind == "held-gate" && i.Detail!.Contains("auto-merge"));
        var noop = items.Single(i => i.Kind == "unverified-noop");
        Assert.Equal("info", noop.Severity);
    }

    [Fact]
    public void Attention_Empty_WhenHealthy()
    {
        var items = NowFeed.BuildAttention(
            new[] { Issue("task-1", IssueStatus.Pending, groomed: "true") },
            new Dictionary<string, bool>(), DateTime.UtcNow);
        Assert.Empty(items);
    }

    [Fact]
    public void Live_StageIsPlainLanguage()
    {
        var now = DateTime.UtcNow;
        var live = NowFeed.BuildLive(
            new[]
            {
                Issue("a", IssueStatus.InProgress, ckpt: DispatchCheckpoint.WorktreeAcquired, updatedAt: now.AddMinutes(-7)),
                Issue("b", IssueStatus.InProgress, ckpt: DispatchCheckpoint.PrOpened, pr: "9"),
            }, now);

        Assert.Equal("agent running", live.Single(l => l.IssueId == "a").Stage);
        Assert.Equal("in review — CI + reviewer", live.Single(l => l.IssueId == "b").Stage);
        Assert.True(live.Single(l => l.IssueId == "a").ElapsedMs >= 7 * 60_000);
    }

    [Fact]
    public void Reason_Rework_BeatsRateLimit_BeatsGrooming()
    {
        var now = DateTime.UtcNow;
        var rework = NowFeed.Reason(Issue("r", IssueStatus.Pending, pr: "42", rework: "1"),
            false, true, "sprint x", null, now);
        Assert.Contains("rework round 2", rework.Reason);

        var limited = NowFeed.Reason(Issue("l", IssueStatus.Pending),
            true, true, "sprint x", "InProgress->Pending err=llm-429", now);
        Assert.Contains("rate limit", limited.Reason);

        var ungroomed = NowFeed.Reason(Issue("u", IssueStatus.Pending),
            false, false, null, null, now);
        Assert.Equal("awaiting technical grooming", ungroomed.Reason);

        var sprinted = NowFeed.Reason(Issue("s", IssueStatus.Pending),
            true, true, "resilience", null, now);
        Assert.Contains("resilience", sprinted.Reason);

        var groomed = NowFeed.Reason(Issue("g", IssueStatus.Pending, groomed: "true"),
            false, false, null, null, now);
        Assert.Equal("groomed — waiting for next sprint", groomed.Reason);
    }
}
