using Forge.Agents;
using Forge.AgentTools;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Projects;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Regression coverage for provider throttling at the production dispatch
/// boundary. These tests stop before any git, GitHub, or model operation.
/// </summary>
public sealed class OrchestratorRateLimitRegressionTests : IDisposable
{
    private readonly string _root = TempRoot.Instance.NewDirectory("orch-429");
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly ProjectDispatchBundle _bundle;
    private readonly ProjectStore _projects;

    public OrchestratorRateLimitRegressionTests()
    {
        _dbPath = Path.Combine(_root, "issues.db");
        _issues = new IssueStore(_dbPath);
        _projects = new ProjectStore(_issues);
        var events = new InMemoryDashboardEventBus();
        var worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = _root, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
        var github = new GitHubService("", "", "");
        var watcher = new PRWatcher(
            github, worktrees, _issues, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30),
            events, NullLogger<PRWatcher>.Instance);

        _bundle = new ProjectDispatchBundle(
            new ProjectOptions { Id = "rate-limit-test", Name = "Rate limit test", Root = _root, DefaultBranch = "main" },
            _issues,
            new AgentStore(_issues),
            new SprintStore(_issues),
            new DesignArtifactStore(_dbPath),
            new ArtOutputStore(_dbPath),
            worktrees,
            github,
            watcher,
            events,
            NullLogger<ProjectDispatchBundle>.Instance);
    }

    [Fact]
    public async Task Typed429_RequeuesPendingWithoutEngineeringStrike()
    {
        var issue = await CreateTaskAsync();
        var dispatcher = new DelegateDispatcher((_, _, _) =>
            throw new LlmRateLimitException(
                "429 rate limit: Token Plan rate limit reached (2062)",
                TimeSpan.FromSeconds(12), RateLimitKind.AccountQuota, "2062", "request-test"));

        var result = await BuildOrchestrator(dispatcher)
            .DispatchSingleTaskAsync(issue, _bundle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("llm-rate-limited", result.Message);
        await AssertPendingWithoutStrikeAsync(issue.Id);
    }

    [Fact]
    public async Task FlattenedFresh429_RequeuesPendingWithoutEngineeringStrike()
    {
        var issue = await CreateTaskAsync();
        var dispatcher = new DelegateDispatcher(async (claimed, bundle, ct) =>
        {
            var current = (await bundle.IssueStore.GetAsync(claimed.Id, ct))!;
            await bundle.IssueStore.TransitionAsync(
                claimed.Id,
                current.Status,
                error: "ClientResultException: HTTP 429 Too Many Requests: rate limit",
                metadata: new Dictionary<string, object>
                {
                    ["lastError"] = "ClientResultException: HTTP 429 Too Many Requests: rate limit",
                    ["lastErrorAt"] = DateTime.UtcNow.ToString("O"),
                },
                ct: ct);
        });

        var result = await BuildOrchestrator(dispatcher)
            .DispatchSingleTaskAsync(issue, _bundle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("llm-rate-limited", result.Message);
        await AssertPendingWithoutStrikeAsync(issue.Id);
    }

    private Task<IssueRecord> CreateTaskAsync()
        => _issues.CreateAsync(new NewIssue(Type: "dev", Title: "provider throttled", Description: "test"));

    private async Task AssertPendingWithoutStrikeAsync(string id)
    {
        var after = (await _issues.GetAsync(id))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        Assert.Null(after.GetMetadata("retryCount"));
        Assert.Null(after.GetMetadata("reworkAttempts"));
        Assert.Null(after.GetMetadata("noProgressAttempts"));
    }

    private OrchestratorAgent BuildOrchestrator(IWorkflowDispatcher dispatcher)
        => new(
            _projects,
            new StubBundleFactory(_bundle),
            new UnusedRunner(),
            new RoleAgentRegistry(),
            new AgentMessageBus(),
            dispatcher,
            _bundle.Events,
            NullLogger<OrchestratorAgent>.Instance);

    public void Dispose() => _issues.Dispose();

    private sealed class DelegateDispatcher(
        Func<IssueRecord, ProjectDispatchBundle, CancellationToken, Task> dispatch) : IWorkflowDispatcher
    {
        public Task DispatchAsync(IssueRecord issue, ProjectDispatchBundle bundle, CancellationToken ct)
            => dispatch(issue, bundle, ct);

        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubBundleFactory(ProjectDispatchBundle bundle) : IProjectDispatchBundleFactory
    {
        public ProjectDispatchBundle Build(ProjectOptions project) => bundle;
    }

    private sealed class UnusedRunner : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentType role,
            string prompt,
            string? sessionId,
            IReadOnlyDictionary<string, object>? context,
            CancellationToken ct)
            => throw new InvalidOperationException("The dispatcher boundary test must not invoke an agent runner.");
    }
}
