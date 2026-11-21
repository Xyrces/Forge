using System.Text.Json;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Dashboard.Services;
using Xunit;

namespace Forge.Tests;

public sealed class TaskPresentationEvidenceTests
{
    private static IssueRecord Task(IssueStatus status, Dictionary<string, object> metadata) => new("task-1", "1", "client", "Fix dashboard", null,
        status, 2, null, DateTime.UtcNow, DateTime.UtcNow, null, JsonSerializer.Serialize(metadata));

    [Fact]
    public void CapturedPr150EvidenceMapsToItsExactRevisionAndOwnCriterion()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pr150-task.json")));
        var meta = source.RootElement.GetProperty("metadata").GetRawText();
        var task = new IssueRecord("client-2", "1", "client", "DevTools", null, IssueStatus.Completed, 2, null,
            DateTime.UtcNow, DateTime.UtcNow, null, meta);
        var result = Forge.Dashboard.TaskDeliveryPresenter.For(task);
        Assert.Equal(4, result.Criteria.Count);
        Assert.Single(result.Criteria, c => c.EvidenceKind == "ImplementationVerified");
        Assert.Equal(3, result.Criteria.Count(c => c.EvidenceKind == "OperatorObserved"));
        Assert.DoesNotContain(result.Criteria, c => c.Text.StartsWith("A documented, repeatable automated check"));
        Assert.StartsWith("Verify the reviewed Forge.UI wiring", result.Criteria[0].Text);
        Assert.Equal("263bbfeb44a12565e3738b01ae08bbc92283ae63", result.VerifiedHead);
        Assert.Equal("30a6ec9f8b740dd4bd2775b26edac0db7ee48997", result.MergeCommit);
        Assert.Equal(DateTimeOffset.Parse("2026-11-18T02:16:03.4094312+00:00"), result.VerifiedAt);
        Assert.Null(result.ExistingSource);
    }

    [Fact]
    public void ExistingCodeRequiresIndependentMatchingPlanBoundEvidence()
    {
        var scope = new SprintScopeReference("spec", "proposal", 1, "hash", "approved");
        var criteria = new[] { new SprintCriterion("code", "Existing behavior", SprintEvidenceKind.ImplementationVerified, scope) };
        var draft = new SprintPlan(1,"project","Outcome",criteria,[new("task-1",["code"],SprintMemberReason.Delivers)],[],"input","");
        var plan = draft with { Hash = SprintPlan.ComputeHash(draft) };
        var now = DateTimeOffset.UtcNow;
        var receipt = new VerificationReceipt("worker",new('a',40),new('b',64),now);
        var review = new ExistingImplementationReview("task-1","project",plan.Hash,new('c',40),receipt.TreeSha,receipt.CommandsSha,["code"],"Approve","independent-reviewer",now.AddSeconds(1));
        var evidence = new VerifiedExistingImplementationEvidence("task-1","project",plan.Hash,new('c',40),criteria,receipt,review);
        var metadata = new Dictionary<string,object> { [ExistingImplementationEvidence.MetadataKey] = JsonSerializer.Serialize(evidence) };
        var task = Task(IssueStatus.Completed,metadata);
        Assert.Equal("Existing implementation verified",TaskSituation.Describe(task,plan).Label);
        Assert.Equal(new string('c',40),Forge.Dashboard.TaskDeliveryPresenter.For(task,plan).ExistingSource);
        Assert.Null(Forge.Dashboard.TaskDeliveryPresenter.For(task).ExistingSource);
        metadata[ExistingImplementationEvidence.MetadataKey] = JsonSerializer.Serialize(evidence with { Review = review with { ReviewerAttemptId = "worker" } });
        Assert.Null(Forge.Dashboard.TaskDeliveryPresenter.For(Task(IssueStatus.Completed,metadata),plan).ExistingSource);
    }

    [Fact]
    public void TaskOverviewShowsOnlyItsOwnCriteriaAndIndependentlyVerifiedRevision()
    {
        var head = new string('a', 40);
        var observed = DateTimeOffset.UtcNow;
        var scope = new SprintScopeReference("spec", "proposal", 1, "hash", "approved");
        var binding = new SprintOutcomeBinding(1, "outcome", "A working dashboard",
            [new("code", "Check the environment", SprintEvidenceKind.ImplementationVerified, scope),
             new("other", "Exercise the mobile board", SprintEvidenceKind.OperatorObserved, scope)],
            ["task-1"], ["code"], SprintMemberReason.Delivers, scope);
        var data = new Dictionary<string, object>
        {
            ["prNumber"] = "150", ["branchSha"] = head, ["sprintOutcome"] = SprintPlanJson.Serialize(binding),
            ["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence("task-1", 150, head, new('b', 40), observed)),
            ["trustedPublication"] = JsonSerializer.Serialize(new TrustedPublicationEvidence("task-1", head,
                new VerificationReceipt("attempt", new('c', 40), new('d', 64), observed)))
        };
        var view = Forge.Dashboard.TaskDeliveryPresenter.For(Task(IssueStatus.Completed, data));
        Assert.Equal("Check the environment", Assert.Single(view.Criteria).Text);
        Assert.Equal(head, view.VerifiedHead);
        Assert.Equal(observed, view.VerifiedAt);
        data["branchSha"] = new string('e', 40);
        data["sprintOutcome"] = SprintPlanJson.Serialize(binding with { RequiredTaskIds = ["another-task"] });
        var incomplete = Forge.Dashboard.TaskDeliveryPresenter.For(Task(IssueStatus.Completed, data));
        Assert.Empty(incomplete.Criteria);
        Assert.Null(incomplete.VerifiedHead);
        Assert.Equal(new string('b', 40), incomplete.MergeCommit);
    }

    [Fact]
    public void CompletedTaskRequiresMatchingObservedMerge()
    {
        var meta = new Dictionary<string, object> { ["prNumber"] = "150", ["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence("task-other", 150, new('a',40), new('b',40), DateTimeOffset.UtcNow)) };
        Assert.Equal("Completion recorded", TaskSituation.Describe(Task(IssueStatus.Completed, meta)).Label);
        meta["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence("task-1", 150, new('a',40), new('b',40), DateTimeOffset.UtcNow));
        Assert.Equal("Merged", TaskSituation.Describe(Task(IssueStatus.Completed, meta)).Label);
    }

    [Fact]
    public void HumanRequirementDoesNotHideActiveRework()
    {
        var meta = new Dictionary<string, object> { ["state"] = "ReworkRunning", ["humanReviewRequired"] = true };
        var result = TaskSituation.Describe(Task(IssueStatus.InProgress, meta));
        Assert.Equal("Making a requested change", result.Label);
        Assert.True(result.HumanReviewRequired);
        meta["awaitingHumanReview"] = true;
        Assert.Equal("Making a requested change", TaskSituation.Describe(Task(IssueStatus.InProgress, meta)).Label);
        meta["state"] = "PROpen";
        Assert.Equal("Waiting for human review", TaskSituation.Describe(Task(IssueStatus.InProgress, meta)).Label);
    }

    [Fact]
    public void HistoricalCancellationDoesNotHideDeliveredOutcomeOrImplyActiveWork()
    {
        var meta = new Dictionary<string, object> { ["lastError"] = "cancelled old worker", ["state"] = "AgentRunning" };
        Assert.Equal("Completion recorded", TaskSituation.Describe(Task(IssueStatus.Completed, meta)).Label);
        Assert.Equal("Needs attention", TaskSituation.Describe(Task(IssueStatus.Failed, meta)).Label);
        meta["humanReviewRequired"] = true;
        meta["state"] = "PROpen";
        Assert.NotEqual("Waiting for human review", TaskSituation.Describe(Task(IssueStatus.InProgress, meta)).Label);
    }

    [Fact]
    public void PlanSectionsPreserveFencedCodeAndUnstructuredProse()
    {
        var parts = WorkPresentation.PlanSections("## Goal\nKeep the extension\n## Verification\n```sh\n# this is a comment\ntest\n```");
        Assert.Equal(2, parts.Count);
        Assert.Equal("Goal", parts[0].Heading);
        Assert.Contains("# this is a comment", parts[1].Body);
        Assert.Single(WorkPresentation.PlanSections("Plan with no headings"));
    }

    [Fact]
    public void UnreadablePlanCritiqueIsWarningAndReviewerSuccessIsNotApproval()
    {
        Assert.True(WorkPresentation.GateWarning("critic returned unparseable verdict (approved with warning)"));
        Assert.Equal("Review completed", WorkPresentation.RunOutcome("succeeded", "Reviewer"));
        Assert.Equal("Run failed", WorkPresentation.RunOutcome("failed", "Reviewer"));
    }

    [Fact]
    public async System.Threading.Tasks.Task WorkerSummaryIsScopedAndExcludesAssignmentSecrets()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("worker-summary"));
        var store = new Forge.Core.Execution.WorkerAttemptStore(issues.Db);
        await store.CreateOrGetAsync(new("one", "dataset-a", "key-one", "project-a", "task-1", "ClientDev", "dispatch", new('a',40), "/work", "secret-assignment", "secret-token"));
        await store.CreateOrGetAsync(new("two", "dataset-b", "key-two", "project-a", "task-1", "ClientDev", "dispatch", new('a',40), "/work", "other-assignment", "other-token"));
        var rows = await store.ListSummariesAsync("dataset-a", "project-a", "task-1");
        Assert.Single(rows);
        Assert.Equal("one", rows[0].Id);
        var output = JsonSerializer.Serialize(rows);
        Assert.DoesNotContain("secret-", output);
        Assert.DoesNotContain("assignment", output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await store.ListSummariesAsync("dataset-a", "project-b", "task-1"));
    }

    [Fact]
    public async System.Threading.Tasks.Task HistoryCursorRemainsScopedAndReadsEarlierEvents()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("task-history"));
        var task = await issues.CreateAsync(new NewIssue("client", "Timeline"));
        for (var i = 0; i < 110; i++) await issues.AddEventAsync(task.Id, "observation", i.ToString());
        var reader = new TaskHistoryReadStore(issues.Db);
        var first = await reader.ReadAsync("project-a", task.Id, null);
        Assert.Equal(100, first.Events.Count);
        Assert.NotNull(first.NextCursor);
        var next = await reader.ReadAsync("project-a", task.Id, first.NextCursor);
        Assert.NotEmpty(next.Events);
        Assert.Empty(first.Events.Select(e => e.Id).Intersect(next.Events.Select(e => e.Id)));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync("project-b", task.Id, first.NextCursor));
    }
}
