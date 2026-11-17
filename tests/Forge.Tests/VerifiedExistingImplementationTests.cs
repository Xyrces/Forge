using System.Net;
using System.Text.Json;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Deploy;
using Forge.Orchestrator;
using Forge.Orchestrator.Sprint;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class VerifiedExistingImplementationTests
{
    private static readonly string Source = new('a', 40);

    private static async Task<(IssueRecord Task, SprintRecord Sprint, SprintPlan Plan, VerifiedExistingImplementationEvidence Evidence)> Seed(IssueStore issues,
        SprintEvidenceKind kind = SprintEvidenceKind.ImplementationVerified)
    {
        var scope = await ApprovedSprintFixture.Scope(issues);
        var task = await ApprovedSprintFixture.Task(issues);
        var criteria = new[] { new SprintCriterion("behavior", "Accepted behavior exists and passes independent checks", kind, scope) };
        await new SprintOutcomeAuthority(issues).BindAsync(task.Id, new(1, "outcome", "Useful outcome", criteria, [task.Id], ["behavior"], SprintMemberReason.Delivers, scope));
        var sprints = new SprintStore(issues);
        var snapshot = await new SprintActivationValidator(issues, sprints, new SpecStore(issues), issues.ProjectId, null).ValidateAsync([task.Id], default);
        var sprint = await sprints.CreateWithIssuesAsync(new("Outcome", "Outcome", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot);
        await issues.TransitionAsync(task.Id, IssueStatus.InProgress, null);
        task = (await issues.GetAsync(task.Id))!;
        var verification = new VerificationReceipt("engineering-attempt", new string('b', 40), new string('c', 64), DateTimeOffset.UtcNow);
        var review = new ExistingImplementationReview(task.Id, issues.ProjectId, snapshot.Plan!.Hash, Source, verification.TreeSha,
            verification.CommandsSha, ["behavior"], "Approve", "independent-review-attempt", verification.CompletedAt.AddSeconds(1));
        return (task, sprint, snapshot.Plan, new(task.Id, issues.ProjectId, snapshot.Plan.Hash, Source, criteria, verification, review));
    }

    [Fact]
    public async Task CanonicalAdmissionPreservesPlanAndCompletesWithoutInventingPullRequest()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("existing-proof"), "project");
        var (task, sprint, plan, evidence) = await Seed(issues);
        await issues.CompleteVerifiedExistingAsync(task.Id, task.UpdatedAt, task.MetadataJson, evidence);
        await issues.CompleteVerifiedExistingAsync(task.Id, task.UpdatedAt, task.MetadataJson, evidence);
        var completed = (await issues.GetAsync(task.Id))!;
        Assert.True(TaskDeliveryEvidence.IsImplementationVerified(completed, plan));
        Assert.False(TaskDeliveryEvidence.IsMergedAndVerified(completed));
        Assert.Null(completed.GetMetadata("prNumber"));
        var receipt = await new CompletionCoordinator(issues, null, null, NullLogger.Instance)
            .CompleteAsync(sprint, new SprintStore(issues), default, routineDeploymentEnabled: true);
        Assert.True(receipt.ReadyForDeployment);
        Assert.Equal(EngineeringOutcome.PendingEvidence, receipt.EngineeringOutcome);
        Assert.Equal(plan.Hash, receipt.PlanHash);
        Assert.Equal(plan.Hash, (await new SprintCompletionStore(issues).GetBoundPlanAsync(receipt)).Hash);
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("wrong-project")]
    [InlineData("wrong-plan")]
    [InlineData("wrong-source")]
    [InlineData("wrong-tree")]
    [InlineData("wrong-commands")]
    [InlineData("same-attempt")]
    [InlineData("missing-criteria")]
    [InlineData("changed-criterion")]
    [InlineData("not-approved")]
    [InlineData("review-before-verification")]
    public async Task InvalidOrLegacyProofCannotCompleteTask(string fault)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("existing-reject"), "project");
        var (task, _, _, evidence) = await Seed(issues, fault == "strict" ? SprintEvidenceKind.MergedAndVerified : SprintEvidenceKind.ImplementationVerified);
        evidence = fault switch
        {
            "wrong-project" => evidence with { ProjectId = "foreign" },
            "wrong-plan" => evidence with { PlanHash = new string('d', 64) },
            "wrong-source" => evidence with { SourceSha = new string('d', 40) },
            "wrong-tree" => evidence with { Review = evidence.Review with { TreeSha = new string('d', 40) } },
            "wrong-commands" => evidence with { Review = evidence.Review with { CommandsSha = new string('d', 64) } },
            "same-attempt" => evidence with { Review = evidence.Review with { ReviewerAttemptId = evidence.Verification.AttemptId } },
            "missing-criteria" => evidence with { Review = evidence.Review with { CriterionIds = [] } },
            "changed-criterion" => evidence with { Criteria = [evidence.Criteria[0] with { Text = "Invented acceptance" }] },
            "not-approved" => evidence with { Review = evidence.Review with { Verdict = "ChangesRequested" } },
            "review-before-verification" => evidence with { Review = evidence.Review with { ReviewedAt = evidence.Verification.CompletedAt.AddSeconds(-1) } },
            _ => evidence
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.CompleteVerifiedExistingAsync(task.Id, task.UpdatedAt, task.MetadataJson, evidence));
        Assert.Equal(task.MetadataJson, (await issues.GetAsync(task.Id))!.MetadataJson);
        Assert.Equal(IssueStatus.InProgress, (await issues.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task StaleTaskCannotOverwriteOperatorAction()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("existing-stale"), "project");
        var (task, _, _, evidence) = await Seed(issues);
        await issues.TransitionAsync(task.Id, IssueStatus.Blocked, "operator stopped");
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.CompleteVerifiedExistingAsync(task.Id, task.UpdatedAt, task.MetadataJson, evidence));
        Assert.Equal(IssueStatus.Blocked, (await issues.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task SourceUsesVerifiedRevisionOnlyWithExactOwningPlanAndRemoteAncestry()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("existing-source"), "project");
        var (task, _, plan, evidence) = await Seed(issues);
        await issues.CompleteVerifiedExistingAsync(task.Id, task.UpdatedAt, task.MetadataJson, evidence);
        task = (await issues.GetAsync(task.Id))!;
        using var handler = new SourceHandler();
        using var source = new TrustedGitHubReleaseSource(new HttpClient(handler), new HttpClient(), (_, _) => Task.FromResult<string?>(null), () => Source);
        var project = new Forge.Configuration.ProjectOptions { Id = "project", RepoUrl = "https://github.com/Xyrces/Forge", DefaultBranch = "main" };
        var result = await source.ObserveMergedFrontierAsync(project, [task], plan, default);
        Assert.Equal(Source, result.Sha);
        Assert.Contains("verifiedExisting", result.Evidence);
        Assert.DoesNotContain("/pulls/", string.Join(";", handler.Paths));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ObserveMergedFrontierAsync(project, [task], default));
        project.Id = "foreign";
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ObserveMergedFrontierAsync(project, [task], plan, default));
    }

    private sealed class SourceHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Assert.EndsWith("/branches/main", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { commit = new { sha = Source } })) });
        }
    }
}
