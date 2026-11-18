using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class ReviewWaitTimingTests
{
    [Fact]
    public async Task ObservedWaitPersistsAcrossRestartAndReleaseDoesNotLoseDuration()
    {
        await using var store = new IssueStore(TempRoot.Instance.NewDbPath("wait-timing"));
        var task = await store.CreateAsync(new("task", "Wait"));
        var start = DateTimeOffset.Parse("2026-11-17T01:00:00Z");
        await store.PatchReviewMetadataAsync(task.Id, ReviewWaitTiming.Observe(task, true, start), null);
        task = (await store.GetAsync(task.Id))!;
        Assert.Empty(ReviewWaitTiming.Observe(task, true, start.AddHours(2)));
        await store.PatchReviewMetadataAsync(task.Id, ReviewWaitTiming.Observe(task, false, start.AddHours(2)), null);
        task = (await store.GetAsync(task.Id))!;
        Assert.Equal(7200, ReviewWaitTiming.CompletedSeconds(task));
        Assert.Empty(ReviewWaitTiming.Observe(task, false, start.AddHours(3)));
        await store.PatchReviewMetadataAsync(task.Id, ReviewWaitTiming.Observe(task, true, start.AddHours(4)), null);
        task = (await store.GetAsync(task.Id))!;
        await store.PatchReviewMetadataAsync(task.Id, ReviewWaitTiming.Observe(task, false, start.AddHours(5)), null);
        Assert.Equal(10800, ReviewWaitTiming.CompletedSeconds((await store.GetAsync(task.Id))!));
    }

    [Fact]
    public async Task EligibilityIsObservedOncePerSprintReworkAndDoesNotChangeOwnership()
    {
        await using var store = new IssueStore(TempRoot.Instance.NewDbPath("eligible-timing"));
        var task = await store.CreateAsync(new("task", "Ready"));
        await store.ObserveDispatchEligibleAsync(task, "sprint-a", default);
        var first = (await store.GetAsync(task.Id))!;
        Assert.NotNull(first.GetMetadata("dispatchEligibleAt"));
        await store.ObserveDispatchEligibleAsync(first, "sprint-a", default);
        Assert.Equal(first.MetadataJson, (await store.GetAsync(task.Id))!.MetadataJson);
        await store.ClaimAsync(task.Id, "worker");
        await store.ObserveDispatchEligibleAsync(first, "sprint-b", default);
        var claimed = (await store.GetAsync(task.Id))!;
        Assert.Equal(first.GetMetadata("dispatchEligibilityKey"), claimed.GetMetadata("dispatchEligibilityKey"));
        Assert.Equal("worker", claimed.Assignee);
    }
}
