using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class DispatchWakeupTimingTests
{
    [Fact]
    public async Task ActivationIsDurableAndRepeatedObservationPreservesOriginalBoundary()
    {
        var path = TempRoot.Instance.NewDbPath("queue-wakeup");
        string id, sprintId;
        await using (var store = new IssueStore(path))
        {
            id = (await store.CreateAsync(new("task", "Work"))).Id;
            await using var sprints = new SprintStore(store);
            var sprint = await sprints.CreateAsync(new("Sprint", "Goal", DateTime.UtcNow, DateTime.UtcNow.AddDays(1), SprintStatus.Completed));
            sprintId = sprint.Id;
            await sprints.AddIssueAsync(sprintId, id);
            await sprints.SetActiveAsync(sprintId);
            await sprints.SetActiveAsync(sprintId);
            Assert.Single((await store.ListEventsAsync(id)).Where(e => e.Kind == "sprint_activated"));
        }
        await using var reopened = new IssueStore(path);
        await reopened.ObserveDispatchEligibleAsync((await reopened.GetAsync(id))!, sprintId, default);
        var measured = DispatchWakeupTiming.Read((await reopened.GetAsync(id))!)!;
        Assert.Equal("active-sprint-selection", measured.Cause);
        Assert.Equal(sprintId, measured.EventDetail);
        Assert.True(measured.IncludesIntentionalWaits);
        Assert.True(measured.ElapsedSeconds >= 0);
        await reopened.ObserveDispatchEligibleAsync((await reopened.GetAsync(id))!, sprintId, default);
        Assert.Equal(measured, DispatchWakeupTiming.Read((await reopened.GetAsync(id))!));
    }

    [Fact]
    public async Task CreationIsUnknownAndEachRealRequeueStartsANewEpisode()
    {
        await using var store = new IssueStore(TempRoot.Instance.NewDbPath("queue-retry"));
        var task = await store.CreateAsync(new("task", "Work"));
        await store.ObserveDispatchEligibleAsync(task, "sprint", default);
        Assert.Null(DispatchWakeupTiming.Read((await store.GetAsync(task.Id))!));
        await store.ClaimAsync(task.Id, "worker");
        await store.TransitionAsync(task.Id, IssueStatus.Pending, null);
        await store.ObserveDispatchEligibleAsync((await store.GetAsync(task.Id))!, "sprint", default);
        var first = DispatchWakeupTiming.Read((await store.GetAsync(task.Id))!)!;
        Assert.Equal("task-status-returned-to-pending", first.Cause);
        await store.ClaimAsync(task.Id, "worker");
        await store.TransitionAsync(task.Id, IssueStatus.Pending, null);
        await store.ObserveDispatchEligibleAsync((await store.GetAsync(task.Id))!, "sprint", default);
        var second = DispatchWakeupTiming.Read((await store.GetAsync(task.Id))!)!;
        Assert.True(second.EventId > first.EventId);
        Assert.Equal("InProgress->Pending", second.EventDetail);
        await store.ObserveDispatchEligibleAsync((await store.GetAsync(task.Id))!, "different-sprint", default);
        Assert.Null(DispatchWakeupTiming.Read((await store.GetAsync(task.Id))!));
    }

    [Fact]
    public async Task WrongSprintAndClaimedTaskDoNotAcquireActivationMeasurement()
    {
        await using var store = new IssueStore(TempRoot.Instance.NewDbPath("queue-scope"));
        var task = await store.CreateAsync(new("task", "Work"));
        await store.AddEventAsync(task.Id, "sprint_activated", "another-sprint");
        await store.ObserveDispatchEligibleAsync(task, "sprint", default);
        Assert.Null(DispatchWakeupTiming.Read((await store.GetAsync(task.Id))!));
        await store.ClaimAsync(task.Id, "worker");
        await store.ObserveDispatchEligibleAsync(task, "another-sprint", default);
        Assert.Null(DispatchWakeupTiming.Read((await store.GetAsync(task.Id))!));
    }
}
