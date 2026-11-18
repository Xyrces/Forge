using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Dashboard;
using Forge.Messaging;
using Forge.Orchestrator;
using Forge.Orchestrator.Consumers;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Talaria.Transports.InMemory;
using Xunit;

namespace Forge.Tests.Messaging;

public sealed class PrWatchReconciliationTests
{
    [Fact]
    public async Task DelayedDuplicateHintReadsGitHub_AndSweepRecoversDroppedSecondHint()
    {
        var path = TempRoot.Instance.NewDbPath("pr-hint");
        await using var issues = new IssueStore(path, "forge");
        var projects = new ProjectStore(issues);
        await projects.UpsertAsync(new("forge", "Forge", "https://github.com/Xyrces/Forge"));
        var first = await issues.CreateAsync(new("task", "First", Metadata: new Dictionary<string, object> { ["prNumber"] = "150" }));
        var second = await issues.CreateAsync(new("task", "Second", Metadata: new Dictionary<string, object> { ["prNumber"] = "151" }));
        await issues.TransitionAsync(first.Id, IssueStatus.Failed, "historic block");
        await issues.TransitionAsync(second.Id, IssueStatus.Failed, "historic block");
        var gh = new MergedGitHub();
        var events = new InMemoryDashboardEventBus();
        var worktrees = new GitWorktreeService(new WorkspaceOptions { Root = Path.GetDirectoryName(path)! }, NullLogger<GitWorktreeService>.Instance);
        var watcher = new PRWatcher(gh, worktrees, issues, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), events, NullLogger<PRWatcher>.Instance);
        var bundle = new ProjectDispatchBundle(new ProjectOptions { Id = "forge", DefaultBranch = "main" },
            issues, new AgentStore(issues), new SprintStore(issues), new DesignArtifactStore(path), new ArtOutputStore(path),
            worktrees, gh, watcher, events, NullLogger.Instance);
        var sweeps = new WatchSweepService(new NeverRunner(), null, null, new ModelRateLimitTracker(), null, null,
            events, NullLoggerFactory.Instance, NullLogger<WatchSweepService>.Instance);
        var transport = new InMemoryTransport();
        var publisher = new TalariaEventPublisher(transport, NullLogger<TalariaEventPublisher>.Instance);
        var consumer = new PrWatchRequestedConsumer(transport, new Bundles(bundle), projects, sweeps, NullLogger<PrWatchRequestedConsumer>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await consumer.StartAsync(deadline.Token);
        try
        {
            var oldReviewHint = new PrWatchRequested { ProjectId = "forge", MessageId = "old-review", PrNumber = 150,
                HeadSha = "obsolete-head", Reason = "github:pull_request_review" };
            await publisher.PublishAsync(oldReviewHint);
            await publisher.PublishAsync(oldReviewHint);
            while ((await issues.GetAsync(first.Id))!.Status != IssueStatus.Completed) await Task.Delay(10, deadline.Token);
            Assert.Equal(IssueStatus.Failed, (await issues.GetAsync(second.Id))!.Status);
            Assert.Null((await issues.GetAsync(first.Id))!.GetMetadata("reworkAttempts"));
            // No webhook for PR151 arrived. Ordinary reconciliation reads its
            // actual merged state and completes it without an invented verdict.
            await sweeps.SweepProjectAsync(bundle, deadline.Token);
            Assert.Equal(IssueStatus.Completed, (await issues.GetAsync(second.Id))!.Status);
            Assert.NotNull((await issues.GetAsync(second.Id))!.GetMetadata("observedMerge"));
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            await sweeps.StopAsync();
        }
    }

    private sealed class Bundles(ProjectDispatchBundle bundle) : IProjectDispatchBundleFactory
    {
        public ProjectDispatchBundle Build(ProjectOptions project)
        {
            Assert.Equal("forge", project.Id);
            return bundle;
        }
    }
    private sealed class NeverRunner : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct)
            => throw new InvalidOperationException("Observed merge must not launch a paid run.");
    }
    private sealed class MergedGitHub() : GitHubService("Xyrces", "Forge", null)
    {
        public override bool RequireFormalReview => false;
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
        {
            var pr = new PullRequest(number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergedAt))!.SetValue(pr, DateTimeOffset.UtcNow);
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))!.SetValue(pr, new string('b', 40));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))!.SetValue(pr, new GitReference(null!, null!, null!, "feature", new string('a', 40), null!, null!));
            return Task.FromResult(pr);
        }
        public override Task<bool> DeleteBranchAsync(string branch, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
