using System.Diagnostics;
using System.Text.Json;
using Forge.Agents;
using Forge.AgentTools;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Orchestrator;
using Forge.Orchestrator.Slots;
using Forge.Orchestrator.Workers;
using Forge.Orchestrator.Workflow;
using Forge.Dashboard;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class ExistingImplementationVerifierTests : IDisposable
{
    private readonly string _root = TempRoot.Instance.NewDirectory("existing-implementation");
    private readonly IssueStore _issues;
    private readonly GitWorktreeService _git;
    private readonly SlotTable _slots = new();
    private readonly FakeGitHub _github = new();
    private readonly ReviewTestJobs _jobs;
    private readonly KubernetesAgentRunner _runner;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ExistingImplementationVerifierTests()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q", "-b", "main"); Git("config", "user.email", "test@example.invalid"); Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_root, "accepted.cs"), "// accepted existing behavior\n");
        Git("add", "."); Git("commit", "-qm", "existing behavior");
        _github.Source = Git("rev-parse", "HEAD").Trim();
        Git("remote", "add", "origin", _root); Git("fetch", "origin"); Git("checkout", "-qb", "agent/existing");
        _issues = new IssueStore(TempRoot.Instance.NewDbPath("existing-implementation"), "project");
        _git = new(new WorkspaceOptions { Root = _root, DefaultBranch = "main" }, NullLogger<GitWorktreeService>.Instance);
        _slots.Configure("project", "reviewer", 1);
        var workers = new Forge.Core.Execution.WorkerAttemptStore(_issues.Db);
        _jobs = new(workers);
        _runner = new(workers, _jobs, "dataset");
        _jobs.OnCreate = a => _jobs.Finish(a, Approve(a));
    }
    private ExistingImplementationVerifier Verifier() => new(_issues, new SprintStore(_issues), _git, _github, _runner, _slots, "project", "main");
    private async Task<(AgentCompleted Input, SprintPlan Plan)> Seed(SprintEvidenceKind kind = SprintEvidenceKind.ImplementationVerified)
    {
        var scope = await ApprovedSprintFixture.Scope(_issues);
        var task = await ApprovedSprintFixture.Task(_issues);
        var criteria = new[] { new SprintCriterion("behavior", "Accepted existing behavior", kind, scope),
            new SprintCriterion("tests", "Existing coverage for the accepted behavior", kind, scope) };
        await new SprintOutcomeAuthority(_issues).BindAsync(task.Id,
            new(1, "outcome", "Useful outcome", criteria, [task.Id], ["behavior", "tests"], SprintMemberReason.Delivers, scope));
        var sprints = new SprintStore(_issues);
        var snapshot = await new SprintActivationValidator(_issues, sprints, new SpecStore(_issues), "project", null).ValidateAsync([task.Id], default);
        await sprints.CreateWithIssuesAsync(new("Outcome", "Outcome", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot);
        task = (await _issues.ClaimAsync(task.Id, "coredev"))!;
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null, new Dictionary<string, object> { ["state"] = "AgentRunning" });
        task = (await _issues.GetAsync(task.Id))!;
        var receipt = await WorkerVerification.VerifyAsync("engineering-job", _root, [], NullLogger.Instance, default);
        return (new(new(new(task, ClaimResult.Ok, _root, "agent/task"), WorktreeResult.Ok, _root, "main"),
            AgentResult.Ok, "NO_CHANGES_NEEDED", null, receipt, true), snapshot.Plan!);
    }
    private static ExistingImplementationVerifier.ReviewRequest Request(Forge.Core.Execution.WorkerAssignment a)
    {
        var invocation = JsonSerializer.Deserialize<WorkerInvocation>(a.AssignmentJson)!;
        var json = invocation.Prompt.Split("Request identity:\n")[1].Split("\nExact approved criteria")[0];
        Assert.Equal(AgentType.Reviewer, invocation.Role);
        Assert.Equal("true", invocation.Context["existingImplementationReview"]);
        return JsonSerializer.Deserialize<ExistingImplementationVerifier.ReviewRequest>(json, Json)!;
    }
    private static string Approve(Forge.Core.Execution.WorkerAssignment a, string fault = "")
    {
        var r = Request(a);
        var reviews = r.CriterionIds.Select(id => new ExistingImplementationVerifier.CriterionReview(id, "Approve", "accepted.cs:1 establishes " + id)).ToArray();
        if (fault == "partial") reviews = reviews.Take(1).ToArray();
        if (fault == "duplicate") reviews = [reviews[0], reviews[0]];
        if (fault == "reject") reviews[0] = reviews[0] with { Verdict = "RequestChanges" };
        return fault == "prose" ? "Everything looks fine, APPROVE" : JsonSerializer.Serialize(new ExistingImplementationVerifier.ReviewResponse(
            r.TaskId, r.ProjectId, r.PlanHash, fault == "source" ? new('f', 40) : r.SourceSha, r.TreeSha, r.CommandsSha,
            fault == "scope" ? new('f', 64) : r.CriteriaHash, r.CriterionIds, "Approve", reviews), Json);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainReviewerOwnershipParksWithoutEngineeringRetryOrCheckoutReset(bool terminal)
    {
        var (input, _) = await Seed();
        _jobs.OnCreate = async a =>
        {
            if (terminal) await _jobs.Finish(a, Approve(a));
            else throw new HttpRequestException("job creation acknowledgment lost");
        };
        if (terminal) _jobs.OnJoin = _ => throw new InvalidOperationException("pod termination unconfirmed");
        async Task<PrOpened> Execute() => await CommitPushPrExecutor.HandleAsync(input, _issues, _git, _github,
            new InMemoryDashboardEventBus(), new NoOpMemoryExtractor(), new MemoryExtractionStore(_issues.Db),
            NullLogger.Instance, null, [], default, lifecycle: new TaskStateMachine(true, NullLogger.Instance),
            verifyExisting: Verifier().TryCompleteAsync);
        Assert.Equal(PrResult.Skipped, (await Execute()).Result);
        var blocked = (await _issues.GetAsync(input.Worktree.Claim.Issue.Id))!;
        Assert.Equal(IssueStatus.Blocked, blocked.Status);
        Assert.Equal("BlockedOperator", blocked.GetMetadata("state"));
        Assert.Null(blocked.GetMetadata("noProgressAttempts"));
        Assert.DoesNotContain(await _issues.ReadyAsync(10), issue => issue.Id == blocked.Id);
        Assert.True(File.Exists(Path.Combine(_root, "accepted.cs")));
        Assert.Equal(PrResult.Skipped, (await Execute()).Result);
        Assert.Single(_jobs.Created);
    }

    [Fact]
    public async Task ReceiptMatchingDirtyTreeCannotProveCodeExistsInRecordedCommit()
    {
        var (input, _) = await Seed();
        File.AppendAllText(Path.Combine(_root, "accepted.cs"), "// uncommitted implementation\n");
        var dirtyReceipt = await WorkerVerification.VerifyAsync("engineering-dirty", _root, [], NullLogger.Instance, default);
        Assert.True(await WorkerVerification.MatchesAsync(dirtyReceipt, _root, [], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Verifier().TryCompleteAsync(input with { Verification = dirtyReceipt }, [], default));
        Assert.Empty(_jobs.Created);
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(input.Worktree.Claim.Issue.Id))!.Status);
    }

    [Fact]
    public async Task NormalPublicationStageUsesIndependentProofAndObservedCompletionWithoutCreatingPr()
    {
        var (input, plan) = await Seed();
        var result = await CommitPushPrExecutor.HandleAsync(input, _issues, _git, _github,
            new InMemoryDashboardEventBus(), new NoOpMemoryExtractor(), new MemoryExtractionStore(_issues.Db),
            NullLogger.Instance, null, [], default, lifecycle: new TaskStateMachine(true, NullLogger.Instance),
            verifyExisting: Verifier().TryCompleteAsync);
        Assert.Equal(PrResult.NoDiff, result.Result);
        var completed = (await _issues.GetAsync(input.Worktree.Claim.Issue.Id))!;
        Assert.Equal("Completed", completed.GetMetadata("state"));
        Assert.Null(completed.GetMetadata("prNumber"));
        Assert.True(ExistingImplementationEvidence.TryGet(completed, plan, out _));
        Assert.Single(_jobs.Created);
    }

    [Fact]
    public async Task IndependentExactReviewCompletesWithoutPr_AndCommittedProofSurvivesMissingLifecycleHint()
    {
        var (input, plan) = await Seed();
        Assert.True(await Verifier().TryCompleteAsync(input, [], default));
        var after = (await _issues.GetAsync(input.Worktree.Claim.Issue.Id))!;
        Assert.True(ExistingImplementationEvidence.TryGet(after, plan, out var proof));
        Assert.Equal(Assert.Single(_jobs.Created).Id, proof!.Review.ReviewerAttemptId);
        Assert.NotEqual(proof.Verification.AttemptId, proof.Review.ReviewerAttemptId);
        Assert.Null(after.Assignee); Assert.Null(after.DispatchCheckpoint); Assert.Null(after.GetMetadata("prNumber"));
        Assert.Equal(TaskLifecycleState.Completed, TaskStateProjector.Derive(after, null, false, DateTime.UtcNow).State);
        Assert.Null(await _issues.ClaimAsync(after.Id, "coredev"));
        await using (var reopened = new IssueStore(_issues.Db, "project"))
        {
            var restored = (await reopened.GetAsync(after.Id))!;
            Assert.True(ExistingImplementationEvidence.TryGet(restored, plan, out _));
            Assert.Equal(TaskLifecycleState.Completed, TaskStateProjector.Derive(restored, null, false, DateTime.UtcNow).State);
            Assert.Null(await reopened.ClaimAsync(restored.Id, "coredev"));
        }
        Assert.Equal(0, _slots.InFlight("project", "reviewer"));
        Assert.True(await Verifier().TryCompleteAsync(input, [], default));
        Assert.Single(_jobs.Created);
    }
    [Theory]
    [InlineData("prose")][InlineData("partial")][InlineData("duplicate")][InlineData("reject")][InlineData("source")][InlineData("scope")]
    public async Task NonExactStructuredAcceptanceNeverCompletes(string fault)
    {
        var (input, _) = await Seed();
        _jobs.OnCreate = a => _jobs.Finish(a, Approve(a, fault));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Verifier().TryCompleteAsync(input, [], default));
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(input.Worktree.Claim.Issue.Id))!.Status);
    }
    [Theory]
    [InlineData("receipt")][InlineData("commands")][InlineData("source")][InlineData("strict")][InlineData("pr")]
    public async Task UnsupportedOrUntrustedInputNeverStartsReviewer(string fault)
    {
        var (input, _) = await Seed(fault == "strict" ? SprintEvidenceKind.MergedAndVerified : SprintEvidenceKind.ImplementationVerified);
        if (fault == "pr") await _issues.TransitionAsync(input.Worktree.Claim.Issue.Id, IssueStatus.InProgress, null, new Dictionary<string, object> { ["prNumber"] = 42 });
        if (fault is "strict" or "pr") Assert.False(await Verifier().TryCompleteAsync(input, [], default));
        else
        {
            if (fault == "receipt") input = input with { Verification = input.Verification! with { TreeSha = new('f', 40) } };
            if (fault == "source") _github.Source = new('f', 40);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Verifier().TryCompleteAsync(input, fault == "commands" ? ["different"] : [], default));
        }
        Assert.Empty(_jobs.Created);
    }
    [Theory]
    [InlineData("source")][InlineData("tree")][InlineData("task")]
    public async Task ChangeDuringReviewCannotCommitProof(string change)
    {
        var (input, _) = await Seed();
        _jobs.OnCreate = async a =>
        {
            if (change == "source") _github.Source = new('f', 40);
            if (change == "tree") File.AppendAllText(Path.Combine(_root, "accepted.cs"), "// changed\n");
            if (change == "task") await _issues.TransitionAsync(input.Worktree.Claim.Issue.Id, IssueStatus.Blocked, "operator parked");
            await _jobs.Finish(a, Approve(a));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Verifier().TryCompleteAsync(input, [], default));
        Assert.NotEqual(IssueStatus.Completed, (await _issues.GetAsync(input.Worktree.Claim.Issue.Id))!.Status);
    }
    [Fact]
    public async Task ControllerInterruptionAdoptsSameIndependentWorkerResult()
    {
        var (input, plan) = await Seed();
        using var stopping = new CancellationTokenSource();
        _jobs.OnCreate = async a => { await _jobs.Finish(a, Approve(a)); stopping.Cancel(); throw new OperationCanceledException(stopping.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Verifier().TryCompleteAsync(input, [], stopping.Token));
        Assert.True(await Verifier().TryCompleteAsync(input, [], default));
        Assert.Single(_jobs.Created);
        Assert.True(ExistingImplementationEvidence.TryGet((await _issues.GetAsync(input.Worktree.Claim.Issue.Id))!, plan, out _));
    }
    private sealed class FakeGitHub() : GitHubService("test", "repo", null)
    {
        public string Source = "";
        public override Task<Octokit.PullRequest> CreatePullRequestAsync(string title, string body, string headBranch,
            string baseBranch = "main", CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Existing implementation must never manufacture a PR.");
        public override Task<string> GetBranchHeadShaAsync(string branch, CancellationToken cancellationToken = default)
        { Assert.Equal("main", branch); return Task.FromResult(Source); }
    }
    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        process.WaitForExit(); Assert.True(process.ExitCode == 0, error); return output;
    }
    public void Dispose()
    {
        _issues.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
