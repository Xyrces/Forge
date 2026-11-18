using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class ReviewMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatorPolicyAndHoldCannotCommitWithoutTheirDurableAudit(bool hold)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-policy-audit"));
        await using var memory = new MemoryStore(issues.Db);
        var policy = new PullRequestReviewPolicy(memory);
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_policy_audit BEFORE INSERT ON memory WHEN NEW.[key] LIKE 'review/%-audit/%' BEGIN SELECT RAISE(ABORT,'audit unavailable'); END";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => hold
            ? policy.SetHoldAsync("task", new(true, true), "operator", default)
            : policy.SetModeAsync(RequiredReview.Both, "operator", default));
        Assert.Equal(RequiredReview.Automated, await policy.GetModeAsync(default));
        Assert.Equal(new HumanReviewHold(false, false), await policy.GetHoldAsync("task", default));
    }

    [Fact]
    public async Task ConcurrentPatchesPreserveEachOtherAndWorkerStatus()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-metadata"));
        var task = await issues.CreateAsync(new("task", "Work", Metadata: new Dictionary<string, object> { ["unrelated"] = "preserved" }));
        await issues.ClaimAsync(task.Id, "worker");
        var before = (await issues.GetAsync(task.Id))!;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => issues.PatchReviewMetadataAsync(task.Id,
            new Dictionary<string, object> { [$"reviewFact{i}"] = i }, $"fact {i}", actor: "operator")));
        var after = (await issues.GetAsync(task.Id))!;
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Assignee, after.Assignee);
        Assert.Equal(before.DispatchCheckpoint, after.DispatchCheckpoint);
        Assert.Equal("preserved", after.GetMetadata("unrelated"));
        for (var i = 0; i < 8; i++) Assert.Equal(i.ToString(), after.GetMetadata($"reviewFact{i}"));
        Assert.Equal(8, (await issues.ListEventsAsync(task.Id)).Count(e => e.Kind == "human_review"));
        await Assert.ThrowsAsync<ArgumentException>(() => issues.PatchReviewMetadataAsync(task.Id,
            new Dictionary<string, object> { ["state"] = "Completed" }, null));
    }

    [Fact]
    public async Task AuditFailureRollsBackMetadataPatch()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-audit"));
        var task = await issues.CreateAsync(new("task", "Work"));
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_review_audit BEFORE INSERT ON issue_event WHEN NEW.kind='human_review' BEGIN SELECT RAISE(ABORT,'audit unavailable'); END";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => issues.PatchReviewMetadataAsync(task.Id,
            new Dictionary<string, object> { ["humanReviewRequired"] = true }, "required"));
        Assert.Null((await issues.GetAsync(task.Id))!.GetMetadata("humanReviewRequired"));
        Assert.Equal(IssueStatus.Pending, (await issues.GetAsync(task.Id))!.Status);
    }
}
