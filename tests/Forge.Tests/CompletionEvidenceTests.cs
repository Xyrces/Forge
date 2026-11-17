using System.Text.Json;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Core.Db;
using Xunit;

namespace Forge.Tests;

public sealed class CompletionEvidenceTests : IDisposable
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("completion-proof"), "project");
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
        Assert.Single(await Store.ListAwaitingDeliveryOutcomeAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RecordOperatorEvidenceAsync(receipt.Id, evidence with { SourceSha = Head }, "authenticated operator"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RecordOperatorEvidenceAsync(receipt.Id, evidence with { CriterionId = "invented" }, "authenticated operator"));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Store.RecordOperatorEvidenceAsync(receipt.Id, evidence, "authenticated operator")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RecordOperatorEvidenceAsync(receipt.Id, evidence with { ProofReference = "different" }, "authenticated operator"));
        Assert.True(await Store.RefreshDeploymentOutcomeAsync(receipt.Id));
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

    public void Dispose() => issues.Dispose();
}
