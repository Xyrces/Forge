using System.Text.Json;
using Forge.Core.Db;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Deploy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class RoutineDeploymentTests : IDisposable
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("routine-deploy"), "forge");
    private readonly FakeSource source = new();
    private readonly FakeExecutor executor = new();
    private SprintCompletionStore Completions => new(issues);
    private DeploymentStore Deployments => new(issues.Db);
    private StageGates Gates => new(new MemoryStore(issues.Db));
    private ProjectOptions Project => new() { Id = "forge", RepoUrl = "https://github.com/Xyrces/Forge", DefaultBranch = "main",
        Deployment = new() { Kind = DeploymentKind.Kubernetes, RoutineEnabled = true } };
    private RoutineDeploymentCoordinator Coordinator() => new(() => [new(Project, Completions, Deployments)], source,
        _ => executor, Gates, NullLogger<RoutineDeploymentCoordinator>.Instance);
    public void Dispose() => issues.Dispose();

    private async Task<SprintCompletionRecord> Complete(bool ready = true, CompletionPolicySnapshot? policy = null)
    {
        var task = await issues.CreateAsync(new NewIssue("task", "deliver", Metadata: new Dictionary<string, object> { ["prNumber"] = 1 }));
        var sprints = new SprintStore(issues);
        var sprint = await sprints.CreateAsync(new("outcome", "outcome", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)));
        await sprints.AddIssueAsync(sprint.Id, task.Id);
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null, new Dictionary<string, object>
        {
            ["prNumber"] = 1,
            ["branchSha"] = FakeSource.Sha,
            ["trustedPublication"] = JsonSerializer.Serialize(new TrustedPublicationEvidence(task.Id, FakeSource.Sha,
                new("attempt", new string('c', 40), new string('d', 64), DateTimeOffset.UtcNow))),
            ["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence(task.Id, 1, FakeSource.Sha, FakeSource.Sha, DateTimeOffset.UtcNow))
        });
        var plan = new SprintPlan(1, "forge", "deliver", [new("deployment", "Deployment is healthy", SprintEvidenceKind.DeploymentHealthy,
            new("spec", "proposal", 1, "hash", "approved delivery"))], [new(task.Id, ["deployment"], SprintMemberReason.Delivers)], [], "snapshot", "");
        plan = plan with { Hash = SprintPlan.ComputeHash(plan) };
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sprint_proposal_audit(ts,weights_json,candidates_json,selected_task_ids_json,committed_sprint_id) VALUES('now',@plan,'[]','[]',@sprint); SELECT last_insert_rowid();";
        command.AddParam("@plan", SprintPlanJson.Serialize(new { plan }));
        command.AddParam("@sprint", sprint.Id);
        var audit = Convert.ToInt64(await command.ExecuteScalarAsync());
        return await Completions.BeginCompletionAsync(sprint.Id, [(await issues.GetAsync(task.Id))!],
            new(audit, plan.Hash, EngineeringOutcome.PendingEvidence, "{}", ready, DeliveryRequired: true, CompletionPolicy: policy));
    }

    [Fact]
    public async Task SuccessfulNamedDeploymentAndSprintClosureShareOneCommit()
    {
        var snapshot = new DeploymentTargetSnapshot("uat", "UAT", "forge-uat", Guid.NewGuid().ToString());
        var policy = new CompletionPolicySnapshot("uat", false, JsonSerializer.Serialize(snapshot, DeploymentStore.EnvironmentJson));
        var completion = await Complete(policy: policy);
        await Completions.MarkBookkeepingCompleteAsync(completion.Id);
        await Coordinator().ReconcileAsync(default);
        var candidate = Assert.Single(await Deployments.ListAsync());
        Assert.Equal(policy.TargetJson, candidate.TargetJson);
        Assert.Equal("uat", candidate.EnvironmentName);
        Assert.Equal(EngineeringOutcome.PendingEvidence, (await Completions.GetAsync(completion.Id))!.EngineeringOutcome);
        await Deployments.MarkIndependentProgressAsync(candidate, new(true, "verified target"), Completions, default);
        Assert.Equal(DeploymentStatus.Deployed, (await Deployments.GetAsync(candidate.Id))!.Status);
        Assert.Equal(EngineeringOutcome.Satisfied, (await Completions.GetAsync(completion.Id))!.EngineeringOutcome);
        Assert.Equal(SprintStatus.Completed, (await new SprintStore(issues).GetAsync(completion.SprintId))!.Status);
    }

    [Fact]
    public async Task DelayedCiRecoversSameCandidateAndCompletesWithoutDashboardReads()
    {
        var completion = await Complete();
        source.Available = false;
        await Coordinator().ReconcileAsync(default);
        var pending = Assert.Single(await Deployments.ListAsync());
        Assert.Equal(DeploymentStatus.Pending, pending.Status);
        Assert.Equal(pending.Id, (await Completions.GetAsync(completion.Id))!.DeploymentCandidateId);
        Assert.Equal(0, executor.Calls);
        source.Available = true;
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(DeploymentStatus.Deploying, (await Deployments.GetAsync(pending.Id))!.Status);
        executor.Result = new(true, "committed");
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(DeploymentStatus.Deployed, (await Deployments.GetAsync(pending.Id))!.Status);
        Assert.Single(await Deployments.ListAsync());
    }

    [Fact]
    public async Task TerminalDeploymentBeforeControllerStopStillAdvancesCompletionAfterRestart()
    {
        var completion = await Complete();
        await Coordinator().ReconcileAsync(default);
        var candidate = Assert.Single(await Deployments.ListAsync());
        await Deployments.MarkIndependentProgressAsync(candidate.Id, new(true, "committed before stop"));
        Assert.Equal(EngineeringOutcome.PendingEvidence, (await Completions.GetAsync(completion.Id))!.EngineeringOutcome);
        Assert.Empty(await Deployments.ListPendingAsync("forge"));
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(EngineeringOutcome.Satisfied, (await Completions.GetAsync(completion.Id))!.EngineeringOutcome);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task AbandonedCompletionCannotCreateOrphanCandidateAndCreationBindsAtomically()
    {
        var completion = await Complete();
        await Completions.RecordSourceAsync(completion.Id, FakeSource.Sha, "{}");
        await Completions.AbandonAsync(completion.Id, completion.PlanHash!, "Operator stopped this outcome", "operator");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Deployments.CreateOrGetRoutineAsync("forge", completion.Id, FakeSource.Sha));
        Assert.Empty(await Deployments.ListAsync());
        var next = await Complete();
        await Completions.RecordSourceAsync(next.Id, FakeSource.Sha, "{}");
        var candidate = await Deployments.CreateOrGetRoutineAsync("forge", next.Id, FakeSource.Sha);
        Assert.Equal(candidate.Id, (await Completions.GetAsync(next.Id))!.DeploymentCandidateId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Completions.AbandonAsync(next.Id, next.PlanHash!, "Stop", "operator"));
    }

    [Fact]
    public async Task ConcurrentInterruptionAndCandidateCreationHaveOnlyOneAuthorityWinner()
    {
        var completion = await Complete();
        await Completions.RecordSourceAsync(completion.Id, FakeSource.Sha, "{}");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Attempt(bool create)
        {
            await start.Task;
            try
            {
                if (create) await Deployments.CreateOrGetRoutineAsync("forge", completion.Id, FakeSource.Sha);
                else await Completions.AbandonAsync(completion.Id, completion.PlanHash!, "Explicit stop", "operator");
                return create ? "created" : "stopped";
            }
            catch (InvalidOperationException) { return "rejected"; }
        }
        var create = Attempt(true);
        var stop = Attempt(false);
        start.SetResult();
        var outcomes = await Task.WhenAll(create, stop);
        Assert.Single(outcomes, value => value == "rejected");
        var receipt = (await Completions.GetAsync(completion.Id))!;
        var candidates = await Deployments.ListAsync();
        if (receipt.AbandonmentJson is not null) Assert.Empty(candidates);
        else Assert.Equal(Assert.Single(candidates).Id, receipt.DeploymentCandidateId);
    }

    [Fact]
    public async Task DeployHoldQualifiesButDoesNotApproveAndReleaseResumes()
    {
        await Complete(); await Gates.HoldAsync(StageGates.Deploy);
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(DeploymentStatus.BuildPassed, Assert.Single(await Deployments.ListAsync()).Status);
        Assert.Equal(0, executor.Calls);
        await Gates.ReleaseAsync(StageGates.Deploy);
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task RestartAfterApprovalHonorsNewHoldUntilJobExists()
    {
        await Complete();
        await Gates.HoldAsync(StageGates.Deploy);
        await Coordinator().ReconcileAsync(default);
        var candidate = Assert.Single(await Deployments.ListAsync());
        await Deployments.TryApproveAsync(candidate.Id, "policy before controller stop");
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(DeploymentStatus.Approved, (await Deployments.GetAsync(candidate.Id))!.Status);
        Assert.Equal(0, executor.Calls);
        await Gates.ReleaseAsync(StageGates.Deploy);
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(1, executor.Calls);
        await Gates.HoldAsync(StageGates.Deploy);
        executor.Result = new(true, "committed");
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(DeploymentStatus.Deployed, (await Deployments.GetAsync(candidate.Id))!.Status);
    }

    [Fact]
    public async Task RepeatedCompletionAndCrashBeforeBindingDoNotCreateDuplicateCandidates()
    {
        var completion = await Complete();
        await Completions.RecordSourceAsync(completion.Id, FakeSource.Sha, "{}");
        var candidate = await Deployments.CreateOrGetRoutineAsync("forge", completion.Id, FakeSource.Sha);
        Assert.Equal(candidate.Id, (await Deployments.CreateOrGetRoutineAsync("forge", completion.Id, FakeSource.Sha)).Id);
        await Coordinator().ReconcileAsync(default);
        Assert.Single(await Deployments.ListAsync());
        Assert.Equal(candidate.Id, (await Completions.GetAsync(completion.Id))!.DeploymentCandidateId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Deployments.CreateOrGetRoutineAsync("forge", completion.Id, new string('c', 40)));
    }

    [Fact]
    public async Task RollbackIsTerminalAndDoesNotAutomaticallyRetry()
    {
        await Complete(); executor.Result = new(false, "rolled-back");
        await Coordinator().ReconcileAsync(default);
        var failed = Assert.Single(await Deployments.ListAsync());
        Assert.Equal(DeploymentStatus.DeployFailed, failed.Status);
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(1, executor.Calls);
        await Deployments.MarkIndependentProgressAsync(failed.Id, new(false, "old pending observation", true));
        Assert.Equal(DeploymentStatus.DeployFailed, (await Deployments.GetAsync(failed.Id))!.Status);
    }

    [Fact]
    public async Task EvidenceNotReadyCannotStartRoutineDelivery()
    {
        await Complete(false);
        await Coordinator().ReconcileAsync(default);
        Assert.Empty(await Deployments.ListAsync());
        Assert.Equal(0, source.Observed);
    }

    [Fact]
    public async Task PendingQueryFindsOlderApprovedOperationBeyondHistoryLimit()
    {
        var old = await Deployments.CreateAsync("forge", FakeSource.Sha, null, "operator");
        await Deployments.TryApproveAsync(old.Id, "operator");
        for (var i = 0; i < 101; i++)
        {
            var other = await Deployments.CreateAsync("forge", FakeSource.Sha, null, "operator");
            await Deployments.MarkDeployFailedAsync(other.Id, "terminal history");
        }
        Assert.Equal(old.Id, Assert.Single(await Deployments.ListPendingAsync("forge")).Id);
        executor.Result = new(true, "committed");
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(DeploymentStatus.Deployed, (await Deployments.GetAsync(old.Id))!.Status);
    }

    private sealed class FakeSource : IRoutineReleaseSource
    {
        public const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public bool Available = true;
        public int Observed;
        public Task<(string Sha, string Evidence)> ObserveMergedFrontierAsync(ProjectOptions project, IReadOnlyList<IssueRecord> members, CancellationToken ct)
        { Observed++; return Task.FromResult((Sha, "{}")); }
        public Task ValidateForwardAsync(ProjectOptions project, string sha, CancellationToken ct) => Task.CompletedTask;
        public Task<TrustedImageReceipt?> FindReceiptAsync(ProjectOptions project, string sha, CancellationToken ct) => Task.FromResult(Available
            ? new TrustedImageReceipt(sha, "ghcr.io/xyrces/forge@sha256:" + new string('b', 64), true, true, "controller-image.yml", "92",
                "Xyrces/Forge", "main", "93", "sha256:" + new string('d', 64), IssueStore.CurrentSchemaVersion, "ci.yml", "91") : null);
    }
    private sealed class FakeExecutor : IIndependentDeploymentExecutor
    {
        public Task<string> ReadTargetRevisionAsync(DeploymentTargetSnapshot target, CancellationToken ct) => Task.FromResult(FakeSource.Sha);
        public int Calls;
        private bool started;
        public DeploymentExecutionResult Result = new(false, "running", true);
        public Task<DeploymentExecutionResult> ExecuteAsync(ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct = default) => throw new InvalidOperationException("Coordinator must use durable progress adoption.");
        public async Task<DeploymentExecutionResult> ReadProgressAsync(ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct, Func<CancellationToken, Task<bool>>? canStart = null)
        {
            if (!started && (canStart is null || !await canStart(ct))) return new(false, "held", true, true);
            started = true;
            Calls++;
            return Result;
        }
    }
}
