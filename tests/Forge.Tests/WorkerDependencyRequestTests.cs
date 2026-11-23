using Forge.AgentTools;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class WorkerDependencyRequestTests
{
    [Fact]
    public async Task RepeatedProposalPreservesDecision_ChangedRationaleRequiresNewReview()
    {
        await using var store = new IssueStore(TempRoot.Instance.NewDbPath("dependency"));
        var issue = await store.CreateAsync(new("task", "Build"));
        var id = await store.RequestWorkerDependencyAsync(issue.Id, "make", "make not found", "Install make in Dockerfile");
        await store.ReviewWorkerDependencyAsync(issue.Id, id, "dismissed");
        Assert.Equal(id, await store.RequestWorkerDependencyAsync(issue.Id, "make", "make not found", "Install make in Dockerfile"));
        var revised = await store.RequestWorkerDependencyAsync(issue.Id, "make", "Native verification also requires make", "Install make in Dockerfile");
        Assert.NotEqual(id, revised);
        var task = (await store.GetAsync(issue.Id))!;
        var requests = WorkerDependencyRequests.Read(task);
        Assert.Equal("dismissed", requests.Single(x => x.Id == id).Status);
        Assert.Equal("pending", requests.Single(x => x.Id == revised).Status);
        Assert.Equal(IssueStatus.Pending, task.Status);
        Assert.Null(task.GetMetadata("groomed"));
        Assert.Single(await store.ListAsync(new()));
    }

    [Fact]
    public async Task ConcurrentProposalsAndReviewRetainEveryRequestAndUnrelatedMetadata()
    {
        await using var store = new IssueStore(TempRoot.Instance.NewDbPath("dependency-concurrent"));
        var issue = await store.CreateAsync(new("task", "Build", Metadata: new Dictionary<string, object> { ["sprintId"] = "sprint-7" }));
        var first = await store.RequestWorkerDependencyAsync(issue.Id, "make", "not found", "add make");
        await Task.WhenAll(store.ReviewWorkerDependencyAsync(issue.Id, first, "acknowledged"),
            store.RequestWorkerDependencyAsync(issue.Id, "make", "not found", "add make"),
            store.RequestWorkerDependencyAsync(issue.Id, "g++", "not found", "add g++"));
        var task = (await store.GetAsync(issue.Id))!;
        Assert.Equal("sprint-7", task.GetMetadata("sprintId"));
        var requests = WorkerDependencyRequests.Read(task);
        Assert.Equal(2, requests.Count);
        Assert.Equal("acknowledged", requests.Single(x => x.Id == first).Status);
    }

    [Fact]
    public async Task DecisionsCannotTargetAnotherTasksProposal()
    {
        await using var store = new IssueStore(TempRoot.Instance.NewDbPath("dependency-scope"));
        var first = await store.CreateAsync(new("task", "First"));
        var second = await store.CreateAsync(new("task", "Second"));
        var id = await store.RequestWorkerDependencyAsync(first.Id, "make", "not found", "add make");
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReviewWorkerDependencyAsync(second.Id, id, "acknowledged"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReviewWorkerDependencyAsync(first.Id, id, "approved"));
        Assert.Empty(WorkerDependencyRequests.Read((await store.GetAsync(second.Id))!));
    }

    [Fact]
    public async Task ToolRejectsInvalidInputWithoutCreatingWork()
    {
        await using var store = new IssueStore(TempRoot.Instance.NewDbPath("dependency-tool"));
        var issue = await store.CreateAsync(new("task", "Build"));
        var tool = new WorkerDependencyTool(store, issue.Id);
        Assert.StartsWith("Invalid dependency proposal", await tool.RequestAsync("", "missing", "install"));
        Assert.Empty(WorkerDependencyRequests.Read((await store.GetAsync(issue.Id))!));
        Assert.StartsWith("Recorded dependency proposal", await tool.RequestAsync("make", "missing", "install"));
        Assert.Single(await store.ListAsync(new()));
        Assert.Equal("request_worker_dependency", tool.AsAIFunction().Name);
    }
}
