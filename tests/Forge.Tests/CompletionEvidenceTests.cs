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
    // Fixture-only injection represents persisted legacy/corrupt projections. Generic writes correctly reject these keys.
    private async Task<IssueRecord> ProjectLegacyMetadataAsync(IssueRecord task, Dictionary<string, object> fields)
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(task.MetadataJson)!.AsObject();
        foreach (var (key, value) in fields) metadata[key] = JsonSerializer.SerializeToNode(value);
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {issues.Db.Dialect.Table("issue")} SET metadata_json=@json WHERE id=@id";
        command.AddParam("@json", metadata.ToJsonString()); command.AddParam("@id", task.Id);
        await command.ExecuteNonQueryAsync();
        return (await issues.GetAsync(task.Id))!;
    }

    private SprintCompletionStore Store => new(issues);
    private static readonly string Head = new('a', 40);
    private static readonly string Source = new('b', 40);

    [Fact]
    public async Task IntegratedHeadAdmissionPreservesOriginalPublicationAndRequiresExactBindings()
    {
        var seeded = await Seed(); var task = seeded.Task;
        var original = task.GetMetadata("trustedPublication");
        var integratedHead = new string('e', 40);
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null, new Dictionary<string, object>
        { ["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence(task.Id, 10, integratedHead, Source, DateTimeOffset.UtcNow)) });
        task = (await issues.GetAsync(task.Id))!;
        Assert.False(TaskDeliveryEvidence.IsMergedAndVerified(task));
        var proof = new IntegratedHeadVerificationEvidence(task.Id, "project", "owner/repo", 10, Head, integratedHead,
            "attempt", new string('f', 40), new("isolated-verifier", new string('1', 40), new string('d', 64), DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.AdmitIntegrationVerificationAsync(task.Id, proof with { ProjectId = "other" }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.AdmitIntegrationVerificationAsync(task.Id, proof with { OriginalAttemptId = "wrong" }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.TransitionAsync(task.Id, IssueStatus.Completed, null,
            new Dictionary<string, object> { ["trustedIntegrationVerification"] = JsonSerializer.Serialize(proof) }));
        await issues.AdmitIntegrationVerificationAsync(task.Id, proof, default);
        task = (await issues.GetAsync(task.Id))!;
        Assert.True(TaskDeliveryEvidence.IsMergedAndVerified(task));
        Assert.Equal(original, task.GetMetadata("trustedPublication"));
        Assert.Equal(Head, task.GetMetadata("branchSha"));
        Assert.Null(TaskDeliveryEvidence.Integration(task, new string('2', 40)));
    }

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
    public async Task LateProofPromotesExactlyOncePreservingFrozenReceiptAndPendingOperatorAcceptance()
    {
        var (sprint, verified, plan, audit) = await Seed();
        var task = await issues.TransitionAsync(verified.Id, IssueStatus.Completed, null,
            new Dictionary<string, object> { ["trustedPublication"] = null! });
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{\"original\":true}", false, true));
        Assert.False(await Store.ReconcileImplementationEvidenceAsync(receipt.Id, [task]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ReadDeliveryMembersAsync(receipt));
        task = await issues.TransitionAsync(task.Id, IssueStatus.Completed, null,
            new Dictionary<string, object> { ["trustedPublication"] = verified.GetMetadata("trustedPublication")! });
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new SprintCompletionStore(issues).ReconcileImplementationEvidenceAsync(receipt.Id, [task])));
        Assert.Single(results.Where(r => r));
        var updated = (await Store.GetAsync(receipt.Id))!;
        Assert.True(updated.ReadyForDeployment);
        Assert.Equal(receipt.MembersJson, updated.MembersJson);
        Assert.Equal(EngineeringOutcome.PendingEvidence, updated.EngineeringOutcome);
        Assert.Null(updated.SourceSha); Assert.Null(updated.DeploymentCandidateId);
        using var evidence = JsonDocument.Parse(updated.EvidenceJson);
        Assert.Equal(receipt.EvidenceJson, evidence.RootElement.GetProperty("deliveryReconciliation").GetProperty("OriginalEvidenceJson").GetString());
        Assert.True(TaskDeliveryEvidence.IsImplementationVerified(Assert.Single(await Store.ReadDeliveryMembersAsync(updated)), plan));
        Assert.False(await new SprintCompletionStore(issues).ReconcileImplementationEvidenceAsync(receipt.Id, [task]));
        Assert.Null(Assert.Single(await Store.ReadCriteriaAsync(receipt.Id)).ProofReference);
    }

    [Fact]
    public async Task NormalReconciliationAcquiresLateProofAndResumesAfterCoordinatorRestart()
    {
        var (sprint, verified, plan, audit) = await Seed();
        var task = await issues.TransitionAsync(verified.Id, IssueStatus.Completed, null,
            new Dictionary<string, object> { ["trustedPublication"] = null! });
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", false, true));
        var calls = 0;
        async Task<bool> Verify(IssueRecord member, CancellationToken ct)
        {
            calls++;
            await issues.TransitionAsync(member.Id, member.Status, "Independent evidence arrived", new Dictionary<string, object>
                { ["trustedPublication"] = verified.GetMetadata("trustedPublication")! }, ct);
            return true;
        }
        await new Forge.Orchestrator.Sprint.CompletionCoordinator(issues, null, null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, deliveryEvidenceReconciler: Verify).ReconcileAsync(default);
        Assert.True((await Store.GetAsync(receipt.Id))!.ReadyForDeployment);
        await new Forge.Orchestrator.Sprint.CompletionCoordinator(issues, null, null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, deliveryEvidenceReconciler: Verify).ReconcileAsync(default);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NoOpVerifierLeavesUsefulStableWaitAndNormalSweepFindsLaterEvidence()
    {
        var (sprint, verified, plan, audit) = await Seed();
        var task = await issues.TransitionAsync(verified.Id, IssueStatus.Completed, null,
            new Dictionary<string, object> { ["trustedPublication"] = null!, ["reviewIntegrationVerificationError"] = "verification command failed" });
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", false, true));
        var coordinator = new Forge.Orchestrator.Sprint.CompletionCoordinator(issues, null, null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, deliveryEvidenceReconciler: (_, _) => Task.FromResult(false));
        await coordinator.ReconcileAsync(default);
        var waiting = (await Store.GetAsync(receipt.Id))!;
        Assert.Contains("verification command failed", waiting.DeliveryNote);
        await coordinator.ReconcileAsync(default);
        Assert.Equal(waiting.UpdatedAt, (await Store.GetAsync(receipt.Id))!.UpdatedAt);
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null, new Dictionary<string, object>
            { ["trustedPublication"] = verified.GetMetadata("trustedPublication")!, ["reviewIntegrationVerificationError"] = null! });
        await coordinator.ReconcileAsync(default);
        Assert.True((await Store.GetAsync(receipt.Id))!.ReadyForDeployment);
        Assert.Null((await Store.GetAsync(receipt.Id))!.DeliveryNote);
    }

    [Fact]
    public async Task ReconciliationCannotReplaceOriginalPublicationOrReadAnotherPlansProof()
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", false, true));
        var original = task.GetMetadata("trustedPublication")!;
        task = await issues.TransitionAsync(task.Id, IssueStatus.Completed, null, new Dictionary<string, object>
            { ["trustedPublication"] = original.Replace("attempt", "different-attempt") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ReconcileImplementationEvidenceAsync(receipt.Id, [task]));
        task = await issues.TransitionAsync(task.Id, IssueStatus.Completed, null, new Dictionary<string, object> { ["trustedPublication"] = original });
        Assert.True(await Store.ReconcileImplementationEvidenceAsync(receipt.Id, [task]));
        var revised = (await Store.GetAsync(receipt.Id))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ReadDeliveryMembersAsync(revised with { EvidenceJson = revised.EvidenceJson.Replace(plan.Hash, "wrong-plan") }));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("membership")]
    [InlineData("status")]
    [InlineData("plan")]
    public async Task ReconciliationRejectsChangedFrozenAuthority(string change)
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", false, true));
        if (change == "stale") await issues.TransitionAsync(task.Id, IssueStatus.Completed, null, new Dictionary<string, object> { ["note"] = "changed" });
        if (change == "status") task = await issues.TransitionAsync(task.Id, IssueStatus.Closed, "changed disposition");
        if (change == "membership")
        {
            var extra = await issues.CreateAsync(new NewIssue("task", "Extra"));
            await new SprintStore(issues).AddIssueAsync(sprint.Id, extra.Id);
        }
        if (change == "plan")
        {
            await using var c = await issues.Db.OpenAsync(); using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sprint_proposal_audit(ts,weights_json,candidates_json,selected_task_ids_json,committed_sprint_id) SELECT ts,weights_json,candidates_json,selected_task_ids_json,committed_sprint_id FROM sprint_proposal_audit WHERE id=@id";
            q.AddParam("@id", audit); await q.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ReconcileImplementationEvidenceAsync(receipt.Id, [task]));
        Assert.False((await Store.GetAsync(receipt.Id))!.ReadyForDeployment);
    }

    [Theory]
    [InlineData("abandoned")]
    [InlineData("source")]
    [InlineData("deployment")]
    public async Task ReconciliationNeverReopensStoppedOrDispatchedCompletion(string state)
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", false, true));
        if (state == "abandoned") await Store.AbandonAsync(receipt.Id, plan.Hash, "Stopped", "operator");
        if (state == "source") await Store.RecordSourceAsync(receipt.Id, Source, "{}");
        if (state == "deployment") await Store.BindDeploymentAsync(receipt.Id, "candidate");
        Assert.False(await Store.ReconcileImplementationEvidenceAsync(receipt.Id, [task]));
    }

    [Fact]
    public async Task InitialReadyCompletionRejectsWorkerProjectedIntegrationAuthority()
    {
        var (sprint, task, plan, audit) = await Seed();
        var h3 = new string('e', 40);
        var proof = new IntegratedHeadVerificationEvidence(task.Id, "project", "https://github.com/owner/repo", 10,
            Head, h3, "attempt", Source, new("new-attempt", new string('f', 40), new string('d', 64), DateTimeOffset.UtcNow));
        task = await ProjectLegacyMetadataAsync(task, new Dictionary<string, object>
        {
            ["trustedIntegrationVerification"] = JsonSerializer.Serialize(proof),
            ["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence(task.Id, 10, h3, Source, DateTimeOffset.UtcNow))
        });
        Assert.True(TaskDeliveryEvidence.IsImplementationVerified(task, plan));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.BeginCompletionAsync(sprint.Id, [task],
            new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", true, true)));
        var receipt = await new Forge.Orchestrator.Sprint.CompletionCoordinator(issues, null, null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).CompleteAsync(sprint, new SprintStore(issues), default, true);
        Assert.False(receipt.ReadyForDeployment);
        using var evidence = JsonDocument.Parse(receipt.EvidenceJson);
        Assert.Empty(evidence.RootElement.GetProperty("verifiedTasks").EnumerateArray());
    }

    [Fact]
    public async Task TwoMemberH3RecoveryPreservesOriginalPublicationAndBecomesSourceEligible()
    {
        var (sprint, task, plan, audit) = await Seed();
        var second = await issues.CreateAsync(new NewIssue("task", "Second independently verified member"));
        second = await issues.TransitionAsync(second.Id, IssueStatus.Completed, null, new Dictionary<string, object>
        {
            ["prNumber"] = 11, ["branchSha"] = Head,
            ["trustedPublication"] = JsonSerializer.Serialize(new TrustedPublicationEvidence(second.Id, Head,
                new("second-attempt", new string('c', 40), new string('d', 64), DateTimeOffset.UtcNow))),
            ["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence(second.Id, 11, Head, Source, DateTimeOffset.UtcNow))
        });
        await new SprintStore(issues).AddIssueAsync(sprint.Id, second.Id);
        plan = plan with { Members = [.. plan.Members, new(second.Id, ["visual"], SprintMemberReason.Delivers)] };
        plan = plan with { Hash = SprintPlan.ComputeHash(plan) };
        await using (var c = await issues.Db.OpenAsync())
        {
            using var q = c.CreateCommand(); q.CommandText = "UPDATE sprint_proposal_audit SET weights_json=@plan WHERE id=@id";
            q.AddParam("@plan", SprintPlanJson.Serialize(new { plan })); q.AddParam("@id", audit); await q.ExecuteNonQueryAsync();
        }
        var h3 = new string('e', 40);
        task = await issues.TransitionAsync(task.Id, IssueStatus.Completed, null, new Dictionary<string, object>
            { ["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence(task.Id, 10, h3, Source, DateTimeOffset.UtcNow)) });
        var originalPublication = task.GetMetadata("trustedPublication");
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task, second], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{\"verifiedTasks\":[]}", false, true));
        Assert.False(await Store.ReconcileImplementationEvidenceAsync(receipt.Id, [task, second]));
        await Store.RecordImplementationWaitAsync(receipt.Id, "Awaiting exact head");
        var before = (await Store.GetAsync(receipt.Id))!;
        await Store.RecordImplementationWaitAsync(receipt.Id, "Awaiting exact head");
        Assert.Equal(before.UpdatedAt, (await Store.GetAsync(receipt.Id))!.UpdatedAt);
        task = await ProjectLegacyMetadataAsync(task, new Dictionary<string, object>
        {
            ["trustedIntegrationVerification"] = JsonSerializer.Serialize(new
            {
                TaskId = task.Id, ProjectId = "project", Repository = "https://github.com/owner/repo",
                PullRequestNumber = 10, OriginalHeadSha = Head, VerifiedHeadSha = h3, OriginalAttemptId = "attempt",
                BaseParentSha = Source, Verification = new VerificationReceipt("integrated-attempt", new string('f', 40), new string('d', 64), DateTimeOffset.UtcNow)
            })
        });
        Assert.False(await Store.ReconcileImplementationEvidenceAsync(receipt.Id, [task, second]));
        await using (var c = await issues.Db.OpenAsync())
        {
            using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO memory([key],body,ts,ttl_days) VALUES(@key,@body,'now',NULL)";
            q.AddParam("@key", TaskDeliveryEvidence.IntegrationAuthorityKey("project", task.Id, h3));
            q.AddParam("@body", task.GetMetadata("trustedIntegrationVerification")!);
            await q.ExecuteNonQueryAsync();
        }
        Assert.True(await Store.ReconcileImplementationEvidenceAsync(receipt.Id, [task, second]));
        var ready = (await Store.GetAsync(receipt.Id))!;
        Assert.Null(ready.DeliveryNote);
        await Store.RecordImplementationWaitAsync(receipt.Id, "stale wait");
        Assert.Null((await Store.GetAsync(receipt.Id))!.DeliveryNote);
        var members = await Store.ReadDeliveryMembersAsync(ready);
        Assert.Equal(2, members.Count);
        await using (var c = await issues.Db.OpenAsync())
        {
            using var q = c.CreateCommand(); q.CommandText = "UPDATE memory SET body=@body WHERE [key]=@key";
            q.AddParam("@body", "{}"); q.AddParam("@key", TaskDeliveryEvidence.IntegrationAuthorityKey("project", task.Id, h3));
            await q.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ReadDeliveryMembersAsync(ready));
            q.Parameters.Clear(); q.AddParam("@body", task.GetMetadata("trustedIntegrationVerification")!);
            q.AddParam("@key", TaskDeliveryEvidence.IntegrationAuthorityKey("project", task.Id, h3)); await q.ExecuteNonQueryAsync();
        }
        Assert.All(members, m => Assert.True(TaskDeliveryEvidence.IsImplementationVerified(m, plan)));
        Assert.Equal(originalPublication, members.Single(m => m.Id == task.Id).GetMetadata("trustedPublication"));
        Assert.Equal(Head, members.Single(m => m.Id == task.Id).GetMetadata("branchSha"));
        using var envelope = JsonDocument.Parse(ready.EvidenceJson);
        Assert.True(envelope.RootElement.TryGetProperty("verifiedTasks", out _));
        Assert.Single(await Store.ListAwaitingDeploymentAsync());
        await Store.RecordSourceAsync(ready.Id, Source, "{\"qualification\":true}");
        Assert.Equal(Source, (await Store.GetAsync(ready.Id))!.SourceSha);
        Assert.Equal(EngineeringOutcome.PendingEvidence, ready.EngineeringOutcome);
    }

    private async Task SeedDeploymentAsync(string completionId, string? environment = null, string? target = null)
    {
        await Store.RecordSourceAsync(completionId, Source, "{}");
        await Store.BindDeploymentAsync(completionId, "deployment");
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO deployment(id,project_id,commit_sha,status,requested_at,completion_id,receipt_json,environment_name,target_json) VALUES('deployment','project',@sha,'Deployed','now',@completion,'{}',@environment,@target)";
        command.AddParam("@sha", Source); command.AddParam("@completion", completionId);
        command.AddParam("@environment", (object?)environment ?? DBNull.Value); command.AddParam("@target", (object?)target ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SprintActivationCapturesPolicyAtomicallyAndNoDeliveryCanComplete()
    {
        var policy = new CompletionPolicySnapshot("uat", true, "{}");
        var sprint = await new SprintStore(issues).CreateAsync(new("Target", "Target", DateTime.UtcNow, DateTime.UtcNow.AddDays(1), CompletionPolicy: policy));
        Assert.Equal(policy, await Store.ReadSprintPolicyAsync(sprint.Id));
    }

    [Fact]
    public async Task ExplicitNoDeliveryPolicyDoesNotTurnAgentObservationIntoDeploymentRequirement()
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.Satisfied, "{}", true, false, new(null, false, null)));
        Assert.Equal(EngineeringOutcome.Satisfied, receipt.EngineeringOutcome);
        Assert.False(receipt.DeliveryRequired);
        Assert.Empty(await Store.ListAwaitingDeploymentAsync());
        Assert.Null(await new SprintStore(issues).GetActiveAsync());
    }

    [Fact]
    public async Task ConfiguredTargetClosesSprintWithoutInventingOperatorObservation()
    {
        var (sprint, task, plan, audit) = await Seed();
        var policy = new CompletionPolicySnapshot("uat", false, "{\"Name\":\"uat\"}");
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", true, true, policy));
        Assert.NotNull(await new SprintStore(issues).GetActiveAsync());
        Assert.True(await Store.CanAdmitDeploymentAsync(receipt.Id));
        Assert.False(Assert.Single(await Store.ReadCriteriaAsync(receipt.Id)).Blocking);
        await SeedDeploymentAsync(receipt.Id, "uat", policy.TargetJson);
        Assert.True(await Store.RefreshDeploymentOutcomeAsync(receipt.Id));
        Assert.Null(await new SprintStore(issues).GetActiveAsync());
        Assert.Null(Assert.Single(await Store.ReadCriteriaAsync(receipt.Id)).ProofReference);
        Assert.Equal(EngineeringOutcome.Satisfied, (await Store.GetAsync(receipt.Id))!.EngineeringOutcome);
    }

    [Fact]
    public async Task WrongEnvironmentCannotSatisfyCapturedTarget()
    {
        var (sprint, task, plan, audit) = await Seed();
        var policy = new CompletionPolicySnapshot("uat", false, "{}");
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", true, true, policy));
        await SeedDeploymentAsync(receipt.Id, "prod", "{}");
        Assert.False(await Store.RefreshDeploymentOutcomeAsync(receipt.Id));
        Assert.NotNull(await new SprintStore(issues).GetActiveAsync());
    }

    [Fact]
    public async Task ConfiguredApprovalIsExplicitAndBoundToCompletionPlan()
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", true, true, new("uat", true, "{}")));
        Assert.False(await Store.CanAdmitDeploymentAsync(receipt.Id));
        Assert.Empty(await Store.ListAwaitingDeploymentAsync());
        Assert.True(Assert.Single(await Store.ListOverviewAsync(waitingOnly: false)).DeploymentApprovalRequired);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ApproveDeploymentAsync(receipt.Id, "wrong-plan", "operator"));
        await Store.ApproveDeploymentAsync(receipt.Id, plan.Hash, "operator");
        Assert.True(await Store.CanAdmitDeploymentAsync(receipt.Id));
        Assert.Single(await Store.ListAwaitingDeploymentAsync());
        Assert.False(Assert.Single(await Store.ListOverviewAsync(waitingOnly: false)).DeploymentApprovalRequired);
    }

    [Fact]
    public async Task LegacyExceptionRecordsMissedCriterionWithoutFalseProofOrChangedEvidence()
    {
        var (sprint, task, plan, audit) = await Seed();
        var receipt = await Store.BeginCompletionAsync(sprint.Id, [task], new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{\"original\":true}", true));
        await SeedDeploymentAsync(receipt.Id);
        var request = new ReconcileDeliveredCompletionRequest(plan.Hash, Source, "deployment", ["visual"], "Preflight was missed; operator accepts the deployed release.");
        Assert.False(await Store.RefreshDeploymentOutcomeAsync(receipt.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ReconcileDeliveredAsync(receipt.Id, request with { MissedCriterionIds = ["invented"] }, "operator"));
        await Store.ReconcileDeliveredAsync(receipt.Id, request, "operator");
        await Store.ReconcileDeliveredAsync(receipt.Id, request, "operator");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ReconcileDeliveredAsync(receipt.Id, request with { Reason = "Different claim" }, "operator"));
        var closed = (await Store.GetAsync(receipt.Id))!;
        Assert.Equal(receipt.EvidenceJson, closed.EvidenceJson);
        Assert.Equal(receipt.MembersJson, closed.MembersJson);
        Assert.Equal(EngineeringOutcome.Satisfied, closed.EngineeringOutcome);
        var criterion = Assert.Single(await Store.ReadCriteriaAsync(receipt.Id));
        Assert.True(criterion.MissedWithException);
        Assert.Null(criterion.ProofReference);
        Assert.Equal(request.Reason, Assert.Single(await Store.ListOverviewAsync(waitingOnly: false)).Reconciliation!.Reason);
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
