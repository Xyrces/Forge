using Forge.Core;
using Forge.Core.Completion;
using Forge.Orchestrator.Sprint;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Forge.Agents;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.Extensions.AI;

namespace Forge.Tests;

public sealed class CompletionCoordinatorTests : IDisposable
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("completion-resume"), "project");

    [Fact]
    public async Task UncertainPaidTriageParksAcrossTicksAndRestartUntilExplicitRetry()
    {
        var task = await issues.CreateAsync(new NewIssue("task", "Done"));
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        var sprints = new SprintStore(issues);
        var sprint = await sprints.CreateAsync(new("Sprint", "Sprint", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)));
        await sprints.AddIssueAsync(sprint.Id, task.Id);
        var drafts = new FollowUpDraftStore(issues);
        for (var i = 0; i < 2; i++)
            await drafts.FileAsync(new(0, sprint.Id, task.Id, "reviewer", "Finding " + i, "Detail", 2, null, DateTime.UtcNow, null));
        var triage = new ThrowingTriage();
        CompletionCoordinator Coordinator() => new(issues, triage, null, NullLogger.Instance);
        var coordinator = Coordinator();
        var receipt = await coordinator.CompleteAsync(sprint, sprints, default);
        await coordinator.ReconcileAsync(default);
        await coordinator.ReconcileAsync(default);
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(1, triage.Calls);
        var store = new SprintCompletionStore(issues);
        var batch = Assert.Single(await store.ListBatchesAsync(receipt.Id));
        Assert.Equal("NeedsReview", batch.State);
        Assert.Empty(await store.ListPlanningBlockersAsync()); // uncertain optional follow-ups do not hold unrelated approved scope
        Assert.Single(await store.ListOverviewAsync()); // the uncertainty remains visible
        Assert.Equal(2, (await drafts.ListUnconsumedAsync()).Count);
        Assert.True(await store.RetryTriageAsync(batch.Id));
        await coordinator.ReconcileAsync(default);
        Assert.Equal(2, triage.Calls);
    }

    private sealed class ThrowingTriage : IFollowUpTriage
    {
        public int Calls;
        public Task<FollowUpTriageDecision?> TriageAsync(string projectId, IReadOnlyList<FollowUpDraft> drafts, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("Paid response outcome uncertain");
        }
    }

    [Theory]
    [InlineData("provider-error")]
    [InlineData("malformed-response")]
    [InlineData("provider-timeout")]
    public async Task RealTriageAgentFailureRetainsSourcesAndRequiresExplicitRetry(string failure)
    {
        var (sprint, sprints, drafts) = await SeedDrafts();
        var client = new FaultClient(failure);
        var triage = RealAgent(client);
        CompletionCoordinator Coordinator() => new(issues, triage, null, NullLogger.Instance);
        var coordinator = Coordinator();
        var receipt = await coordinator.CompleteAsync(sprint, sprints, default);

        await coordinator.ReconcileAsync(default);
        await coordinator.ReconcileAsync(default);
        await Coordinator().ReconcileAsync(default);

        var store = new SprintCompletionStore(issues);
        var batch = Assert.Single(await store.ListBatchesAsync(receipt.Id));
        Assert.Equal("NeedsReview", batch.State);
        Assert.Null(batch.DecisionJson);
        Assert.Equal(2, (await drafts.ListUnconsumedAsync()).Count);
        Assert.Equal(1, client.Calls);
        Assert.True(await store.RetryTriageAsync(batch.Id));
        await coordinator.ReconcileAsync(default);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task RealAgentSuccessfulEmptyDecisionRetainsDocumentedUnmentionedSourceFallback()
    {
        var (sprint, sprints, drafts) = await SeedDrafts();
        var client = new FaultClient("success");
        var coordinator = new CompletionCoordinator(issues, RealAgent(client), null, NullLogger.Instance);
        var receipt = await coordinator.CompleteAsync(sprint, sprints, default);
        await coordinator.ReconcileAsync(default);
        var batch = Assert.Single(await new SprintCompletionStore(issues).ListBatchesAsync(receipt.Id));
        Assert.Equal("Completed", batch.State);
        Assert.NotNull(batch.DecisionJson);
        Assert.Empty(await drafts.ListUnconsumedAsync());
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task RealAgentShutdownCancellationPropagatesAndRestartParksUncertainBatch()
    {
        var (sprint, sprints, drafts) = await SeedDrafts();
        using var cancellation = new CancellationTokenSource();
        var client = new FaultClient("cancel", cancellation);
        var triage = RealAgent(client);
        var coordinator = new CompletionCoordinator(issues, triage, null, NullLogger.Instance);
        var receipt = await coordinator.CompleteAsync(sprint, sprints, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ReconcileAsync(cancellation.Token));
        await new CompletionCoordinator(issues, triage, null, NullLogger.Instance).ReconcileAsync(default);
        var batch = Assert.Single(await new SprintCompletionStore(issues).ListBatchesAsync(receipt.Id));
        Assert.Equal("NeedsReview", batch.State);
        Assert.Null(batch.DecisionJson);
        Assert.Equal(2, (await drafts.ListUnconsumedAsync()).Count);
        Assert.Equal(1, client.Calls);
    }

    private async Task<(SprintRecord, SprintStore, FollowUpDraftStore)> SeedDrafts()
    {
        var task = await issues.CreateAsync(new NewIssue("task", "Done"));
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        var sprints = new SprintStore(issues);
        var sprint = await sprints.CreateAsync(new("Sprint", "Sprint", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)));
        await sprints.AddIssueAsync(sprint.Id, task.Id);
        var drafts = new FollowUpDraftStore(issues);
        for (var i = 0; i < 2; i++)
            await drafts.FileAsync(new(0, sprint.Id, task.Id, "reviewer", "Finding " + i, "Detail", 2, null, DateTime.UtcNow, null));
        return (sprint, sprints, drafts);
    }

    private static FollowUpTriageAgent RealAgent(IChatClient client) => new(new ScriptingChatClientFactory(client),
        new LlmConfig(new ProviderConfig("test", "", null, null, "test")), NullLogger<FollowUpTriageAgent>.Instance);

    private sealed class FaultClient(string failure, CancellationTokenSource? cancellation = null)
        : DelegatingChatClient(new MultiToolCallingChatClient([], "unused"))
    {
        public int Calls;
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (failure == "provider-error") throw new HttpRequestException("Provider failed after submission");
            if (failure == "provider-timeout") throw new OperationCanceledException("Provider deadline, not controller shutdown");
            if (failure == "cancel") cancellation!.Cancel();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                failure is "cancel" or "success" ? "[]" : "No structured decision")));
        }
    }
    public void Dispose() => issues.Dispose();
}
