using Microsoft.SemanticKernel;
using Octokit;
using PortHorizon.Agents.Core;
using static Octokit.CommitState;

namespace PortHorizon.Agents;

public sealed class ReviewerAgent : IAgent
{
    private readonly Kernel _kernel;
    private readonly AgentConfig _config;
    private readonly StateStore _stateStore;
    private readonly GitHubService _gitHubService;

    public string Id => "reviewer";
    public string Name => "ReviewerAgent";
    public AgentType Type => AgentType.Reviewer;
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;

    public ReviewerAgent(Kernel kernel, AgentConfig config, StateStore stateStore, GitHubService gitHubService)
    {
        _kernel = kernel;
        _config = config;
        _stateStore = stateStore;
        _gitHubService = gitHubService;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Status = AgentStatus.Running;
        try
        {
            var state = await _stateStore.LoadStateAsync(cancellationToken);
            var pendingReviews = state.Tasks.Where(t => t.Type == "review" && t.Status == AgentTaskStatus.Pending).ToList();

            foreach (var task in pendingReviews)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await ProcessTaskAsync(task, cancellationToken);
            }
            Status = AgentStatus.Idle;
        }
        catch
        {
            Status = AgentStatus.Error;
            throw;
        }
    }

    public async Task<Result> ProcessTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        Status = AgentStatus.Running;
        try
        {
            var prNumber = task.Parameters.GetValueOrDefault("prNumber") as int? ?? 0;
            var branch = task.Parameters.GetValueOrDefault("branch") as string ?? task.Branch;

            var checks = await _gitHubService.GetCommitStatusAsync(branch, cancellationToken);
            if (checks != CommitState.Success)
                return new Result(false, $"CI not passed for PR #{prNumber}");

            var reviews = await _gitHubService.GetReviewsAsync(prNumber, cancellationToken);
            if (!reviews.Any(r => r.State == PullRequestReviewState.Approved))
                return new Result(false, $"No approval for PR #{prNumber}");

            var merged = await _gitHubService.MergePullRequestAsync(prNumber, cancellationToken);
            if (!merged)
                return new Result(false, $"Failed to merge PR #{prNumber}");

            task = task with { Status = AgentTaskStatus.Completed, CompletedAt = DateTime.UtcNow };
            var state = await _stateStore.LoadStateAsync(cancellationToken);
            var idx = state.Tasks.FindIndex(t => t.Id == task.Id);
            if (idx >= 0) state.Tasks[idx] = task;
            await _stateStore.SaveStateAsync(state, cancellationToken);

            Status = AgentStatus.Idle;
            return new Result(true, $"PR #{prNumber} merged successfully");
        }
        catch (Exception ex)
        {
            Status = AgentStatus.Error;
            return new Result(false, ex.Message);
        }
    }
}