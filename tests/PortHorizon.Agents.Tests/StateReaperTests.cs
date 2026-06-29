using PortHorizon.Agents.Core;
using Xunit;

namespace PortHorizon.Agents.Tests;

public class StateReaperTests
{
    [Fact]
    public void ReapStaleTasks_InProgressBeyondThreshold_ResetToPending()
    {
        var task = new AgentTask(
            Id: "t-1", Type: "ecs", Description: "d",
            Parameters: new Dictionary<string, object>(),
            Branch: "agent/t-1", Status: AgentTaskStatus.InProgress,
            Error: null, CreatedAt: DateTime.UtcNow.AddHours(-2),
            UpdatedAt: DateTime.UtcNow.AddHours(-2));
        var state = new OrchestratorState();
        state.Tasks.Add(task);

        var swept = StateReaper.ReapStaleTasks(state, TimeSpan.FromMinutes(30), maxRetryCount: 1, worktreeExists: null);

        var t = Assert.Single(swept.Tasks);
        Assert.Equal(AgentTaskStatus.Pending, t.Status);
        Assert.NotNull(t.Error);
        Assert.Contains("stale", t.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, t.Parameters["retryCount"]);
    }

    [Fact]
    public void ReapStaleTasks_RetryBudgetExhausted_Fails()
    {
        var task = new AgentTask(
            Id: "t-1", Type: "ecs", Description: "d",
            Parameters: new Dictionary<string, object> { ["retryCount"] = 1 },
            Branch: "agent/t-1", Status: AgentTaskStatus.InProgress,
            Error: null, CreatedAt: DateTime.UtcNow.AddHours(-2),
            UpdatedAt: DateTime.UtcNow.AddHours(-2));
        var state = new OrchestratorState();
        state.Tasks.Add(task);

        var swept = StateReaper.ReapStaleTasks(state, TimeSpan.FromMinutes(30), maxRetryCount: 1, worktreeExists: null);
        var t = Assert.Single(swept.Tasks);
        Assert.Equal(AgentTaskStatus.Failed, t.Status);
    }

    [Fact]
    public void ReapStaleTasks_FreshTask_LeftAlone()
    {
        var task = new AgentTask(
            Id: "t-1", Type: "ecs", Description: "d",
            Parameters: new Dictionary<string, object>(),
            Branch: "agent/t-1", Status: AgentTaskStatus.InProgress,
            Error: null, CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);
        var state = new OrchestratorState();
        state.Tasks.Add(task);

        var swept = StateReaper.ReapStaleTasks(state, TimeSpan.FromMinutes(30), maxRetryCount: 1, worktreeExists: null);
        var t = Assert.Single(swept.Tasks);
        Assert.Equal(AgentTaskStatus.InProgress, t.Status);
        Assert.Null(t.Error);
    }

    [Fact]
    public void ReapStaleTasks_NonInProgress_LeftAlone()
    {
        var task = new AgentTask(
            Id: "t-1", Type: "ecs", Description: "d",
            Parameters: new Dictionary<string, object>(),
            Branch: "agent/t-1", Status: AgentTaskStatus.Completed,
            Error: null, CreatedAt: DateTime.UtcNow.AddHours(-3),
            UpdatedAt: DateTime.UtcNow.AddHours(-3));
        var state = new OrchestratorState();
        state.Tasks.Add(task);

        var swept = StateReaper.ReapStaleTasks(state, TimeSpan.FromMinutes(30), maxRetryCount: 1, worktreeExists: null);
        var t = Assert.Single(swept.Tasks);
        Assert.Equal(AgentTaskStatus.Completed, t.Status);
    }
}