using System.Text.Json;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Core.Db;
using Xunit;

namespace Forge.Tests;

public sealed class CompletionEvidenceTests : IDisposable
{
    private readonly IssueStore issues;
    private readonly OutcomeEvents events = new();
    public CompletionEvidenceTests() => issues = new(TempRoot.Instance.NewDbPath("completion-proof"), "project", events);
    private sealed class OutcomeEvents : Forge.Core.Messaging.IEventPublisher
    {
        public List<Forge.Core.Messaging.SprintOutcomeChanged> Outcomes { get; } = [];
        public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : Forge.Core.Messaging.IForgeEvent
        {
            if (evt is Forge.Core.Messaging.SprintOutcomeChanged outcome) Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }
    private SprintCompletionStore Store => new(issues);
    private static readonly string Head = new('a', 40);
    private static readonly string Source = new('b', 40);

    private async Task<(SprintRecord Sprint, IssueRecord Task, SprintPlan Plan, long Audit)> Seed()
    {
        var task = await issues.CreateAsync(new NewIssue("task", "Verified implementation"));
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null, new Dictionary<string, object>
        {
            ["prNumber"] = 10, ["branchSha"] = Head,
            ["trustedPublication"] = JsonSerializer.Serialize(new TrustedPublicationEvidence(task.Id, Head,
                new("attempt", new string('c', 40), new string('d', 64), DateTimeOffset.UtcNow))),
            ["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence(task.Id, 10, Head, Source, DateTimeOffset.UtcNow))
        });
        task = (await issues.GetAsync(task.Id))!;
        var sprints = new SprintStore(issues);
        var sprint = await sprints.CreateAsync(new("Outcome", "Outcome", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)));
        await sprints.AddIssueAsync(sprint.Id, task.Id);
        var plan = new SprintPlan(1, "project", "Verify layout", [new("visual", "Layout remains readable",
            SprintEvidenceKind.OperatorObserved, new("spec", "proposal", 1, "hash", "Approved layout"))],
            [new(task.Id, ["visual"], SprintMemberReason.Delivers)], [], "snapshot", "");
        plan = plan with { Hash = SprintPlan.ComputeHash(plan) };
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sprint_proposal_audit(ts,weights_json,candidates_json,selected_task_ids_json,committed_sprint_id) VALUES('now',@json,'[]','[]',@sprint); SELECT last_insert_rowid();";
        command.AddParam("@json", SprintPlanJson.Serialize(new { plan }));
        command.AddParam("@sprint", sprint.Id);
        return (sprint, task, plan, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ExactDeployedCriterionRequiresExplicitImmutableOperatorEvidence()
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", true));
        await Store.RecordSourceAsync(receipt.Id, Source, "{\"qualified\":true}");
        await Store.BindDeploymentAsync(receipt.Id, "deployment");
        var evidence = new OperatorCriterionEvidence(plan.Hash, "visual", Source, "deployment", "proof/layout.png", new string('e', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RecordOperatorEvidenceAsync(receipt.Id, evidence, "authenticated operator"));
        await using (var connection = await issues.Db.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO deployment(id,project_id,commit_sha,status,requested_at,completion_id,receipt_json) VALUES('deployment','project',@sha,'Deployed','now',@completion,'{}')";
            command.AddParam("@sha", Source); command.AddParam("@completion", receipt.Id);
            await command.ExecuteNonQueryAsync();
        }
        Assert.False(await Store.RefreshDeploymentOutcomeAsync(receipt.Id));
        Assert.Empty(events.Outcomes);
        Assert.Single(await Store.ListAwaitingDeliveryOutcomeAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RecordOperatorEvidenceAsync(receipt.Id, evidence with { SourceSha = Head }, "authenticated operator"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RecordOperatorEvidenceAsync(receipt.Id, evidence with { CriterionId = "invented" }, "authenticated operator"));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Store.RecordOperatorEvidenceAsync(receipt.Id, evidence, "authenticated operator")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RecordOperatorEvidenceAsync(receipt.Id, evidence with { ProofReference = "different" }, "authenticated operator"));
        Assert.True(await Store.RefreshDeploymentOutcomeAsync(receipt.Id));
        Assert.False(await Store.RefreshDeploymentOutcomeAsync(receipt.Id));
        var accepted = Assert.Single(events.Outcomes);
        Assert.Equal("Satisfied", accepted.Outcome);
        Assert.Equal("project", accepted.ProjectId);
        Assert.Equal(sprint.Id, accepted.SprintId);
        Assert.Empty(await Store.ListAwaitingDeliveryOutcomeAsync());
        Assert.Equal(EngineeringOutcome.Satisfied, (await Store.GetAsync(receipt.Id))!.EngineeringOutcome);
    }

    [Fact]
    public async Task ChangedLatestPlanCannotBeCompletedFromStaleEvaluation()
    {
        var (sprint, task, plan, audit) = await Seed();
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sprint_proposal_audit(ts,weights_json,candidates_json,selected_task_ids_json,committed_sprint_id) SELECT ts,weights_json,candidates_json,selected_task_ids_json,committed_sprint_id FROM sprint_proposal_audit WHERE id=@id";
        command.AddParam("@id", audit); await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", true)));
        Assert.Equal(SprintStatus.Active, (await new SprintStore(issues).GetAsync(sprint.Id))!.Status);
        Assert.Null(await Store.GetAsync("completion-" + sprint.Id));
    }

    [Fact]
    public async Task CompletedAloneMalformedOrWrongHeadCannotClaimVerification()
    {
        var (_, task, _, _) = await Seed();
        Assert.True(TaskDeliveryEvidence.IsMergedAndVerified(task));
        Assert.False(TaskDeliveryEvidence.IsMergedAndVerified(task with { MetadataJson = "{}" }));
        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(task.MetadataJson)!;
        metadata["branchSha"] = Source;
        Assert.False(TaskDeliveryEvidence.IsMergedAndVerified(task with { MetadataJson = JsonSerializer.Serialize(metadata) }));
        metadata["trustedPublication"] = "{\"TaskId\":\"x\",\"Verification\":null}";
        Assert.False(TaskDeliveryEvidence.IsMergedAndVerified(task with { MetadataJson = JsonSerializer.Serialize(metadata) }));
    }

    [Fact]
    public async Task ExplicitAbandonmentResolvesBarrierWithoutClaimingDeliveryOrAdmittingDeployment()
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", true));
        await Store.MarkBookkeepingCompleteAsync(receipt.Id);
        Assert.Single(await Store.ListPlanningBlockersAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AbandonAsync(receipt.Id, "different-plan", "Stop this scope", "authenticated operator"));
        await Store.AbandonAsync(receipt.Id, plan.Hash, "Stop this scope", "authenticated operator");
        await Store.AbandonAsync(receipt.Id, plan.Hash, "Stop this scope", "authenticated operator");
        Assert.Equal("Abandoned", Assert.Single(events.Outcomes).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AbandonAsync(receipt.Id, plan.Hash, "Changed reason", "authenticated operator"));
        await Store.MarkBookkeepingCompleteAsync(receipt.Id); // stale reconciliation cannot overwrite the explicit stop
        Assert.Equal("Stopped", (await Store.GetAsync(receipt.Id))!.BookkeepingState);
        Assert.Empty(await Store.ListPlanningBlockersAsync());
        Assert.Empty(await Store.ListAwaitingDeploymentAsync());
        Assert.Equal(EngineeringOutcome.PendingEvidence, (await Store.GetAsync(receipt.Id))!.EngineeringOutcome);
        Assert.Equal("Stop this scope", Assert.Single(await Store.ListOverviewAsync(waitingOnly: false)).AbandonmentReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.BindDeploymentAsync(receipt.Id, "new-release"));
    }

    [Fact]
    public async Task OperatorCannotAbandonAnIndependentUnfinishedRelease()
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", true));
        await Store.BindDeploymentAsync(receipt.Id, "release");
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO deployment(id,project_id,commit_sha,status,requested_at,completion_id) VALUES('release','project','source','Deploying','now',@completion)";
        command.AddParam("@completion", receipt.Id);
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AbandonAsync(receipt.Id, plan.Hash, "Stop", "authenticated operator"));
        Assert.Null((await Store.GetAsync(receipt.Id))!.AbandonmentJson);
        command.CommandText = "UPDATE deployment SET status='DeployFailed' WHERE id='release'";
        await command.ExecuteNonQueryAsync();
        await Store.AbandonAsync(receipt.Id, plan.Hash, "Stop", "authenticated operator");
        Assert.NotNull((await Store.GetAsync(receipt.Id))!.AbandonmentJson);
    }

    [Fact]
    public async Task AbandonmentStopsUncertainBookkeepingButDoesNotRaceRunningTriage()
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", true));
        var drafts = new FollowUpDraftStore(issues);
        var id = await drafts.FileAsync(new(0, sprint.Id, task.Id, "reviewer", "Finding", "Detail", 2, null, DateTime.UtcNow, null));
        var batch = await Store.ClaimBatchAsync(receipt.Id, [(await drafts.GetAsync(id))!]);
        await Store.TryBeginTriageAsync(batch.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AbandonAsync(receipt.Id, plan.Hash, "Stop", "operator"));
        await Store.RecoverRunningAsync();
        await Store.AbandonAsync(receipt.Id, plan.Hash, "Stop", "operator");
        Assert.False(await Store.RetryTriageAsync(batch.Id));
        Assert.False(await Store.TryBeginTriageAsync(batch.Id));
        Assert.Empty(await Store.ListPendingBookkeepingAsync());
        Assert.Empty(await Store.ListPlanningBlockersAsync());
        Assert.Single(await drafts.ListUnconsumedAsync());
        Assert.Equal("Stopped", (await Store.GetAsync(receipt.Id))!.BookkeepingState);
    }

    public void Dispose() => issues.Dispose();
}
