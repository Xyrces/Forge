using Forge.Core;
using Forge.Core.Workflow;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Pass 3 policy plumbing: typed readers fall back to code
/// constants, and the projector honors injected policy values.
/// Machinery-level behavior (breaker / park / auto-merge) is covered
/// in PRWatcherReworkTests.
/// </summary>
public sealed class WorkflowPolicyTests
{
    private static WorkflowDefinition DefWith(string key, string value)
        => WorkflowDefaults.Definition with
        {
            Policies = new Dictionary<string, string>(WorkflowDefaults.Definition.Policies) { [key] = value },
        };

    [Fact]
    public void Reader_ValuesWin_FallbacksHold()
    {
        var d = DefWith(WorkflowPolicies.MaxStrikes, "7");
        Assert.Equal(7, WorkflowPolicyReader.GetInt(d, WorkflowPolicies.MaxStrikes, 3));
        Assert.Equal(3, WorkflowPolicyReader.GetInt(d, "absent", 3));

        var b = DefWith(WorkflowPolicies.AutoMerge, "false");
        Assert.False(WorkflowPolicyReader.GetBool(b, WorkflowPolicies.AutoMerge, true));
        Assert.True(WorkflowPolicyReader.GetBool(b, "absent", true));

        // Corrupt values (hand-edited memory keys) fall back too.
        var corrupt = DefWith(WorkflowPolicies.MaxStrikes, "lots");
        Assert.Equal(3, WorkflowPolicyReader.GetInt(corrupt, WorkflowPolicies.MaxStrikes, 3));
    }

    [Fact]
    public void Projector_MaxStrikesOverride_Reflected()
    {
        var task = Issue(IssueStatus.Pending);
        var info = TaskStateProjector.Derive(task, null, false, DateTime.UtcNow, maxStrikes: 5);
        Assert.Equal(5, info.MaxStrikes);
        Assert.Equal(TaskStateProjector.MaxStrikes,
            TaskStateProjector.Derive(task, null, false, DateTime.UtcNow).MaxStrikes);
    }

    [Fact]
    public void Projector_StallGraceOverride_ChangesStallClassification()
    {
        // Claimed rework round, untouched for 10 minutes: inside the
        // default 35m grace (starting), outside a 5m override (stalled).
        var task = Issue(IssueStatus.InProgress, pr: "42") with { UpdatedAt = DateTime.UtcNow.AddMinutes(-10) };
        var fresh = TaskStateProjector.Derive(task, null, false, DateTime.UtcNow);
        Assert.Equal(TaskLifecycleState.ReworkRunning, fresh.State);
        var stale = TaskStateProjector.Derive(task, null, false, DateTime.UtcNow,
            stallGrace: TimeSpan.FromMinutes(5));
        Assert.Equal(TaskLifecycleState.StalledRework, stale.State);
    }

    private static IssueRecord Issue(IssueStatus status, string? pr = null)
        => new(
            Id: "task-1", ShortId: "1", Type: "task", Title: "t",
            Description: null, Status: status, Priority: 2, Assignee: null,
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, ClosedAt: null,
            MetadataJson: pr is not null
                ? System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["prNumber"] = pr,
                    ["reworkReason"] = "CI failed",
                })
                : "{}",
            ParentIssueId: null, DispatchCheckpoint: null);
}
