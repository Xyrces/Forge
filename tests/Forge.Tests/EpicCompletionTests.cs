using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>EpicCompletion: the pure terminal-tree rule.</summary>
public class EpicCompletionTests
{
    private static IssueRecord Epic(string id = "epic-1", IssueStatus status = IssueStatus.Pending) => new(
        Id: id, ShortId: id, Type: "epic", Title: "e", Description: null,
        Status: status, Priority: 2, Assignee: null,
        CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, ClosedAt: null, MetadataJson: "{}");

    private static SpecRecord Spec(string id, string epicId, SpecStatus status) => new(
        Id: id, ProjectId: "p", Title: "s", Status: status, ParentIssueId: epicId,
        ParentSpecId: null, CurrentVersion: 1, CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow,
        Body: "", Author: "t");

    private static IssueRecord Issue(string id, string type, string? parent, IssueStatus status) => new(
        Id: id, ShortId: id, Type: type, Title: "t", Description: null,
        Status: status, Priority: 2, Assignee: null,
        CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, ClosedAt: null, MetadataJson: "{}",
        ParentIssueId: parent);

    private static IssueRecord Watch(string id, string taskId, IssueStatus status) => new(
        Id: id, ShortId: id, Type: AgentTaskTypes.PrWatch, Title: "w", Description: null,
        Status: status, Priority: 2, Assignee: null,
        CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, ClosedAt: null,
        MetadataJson: System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object> { ["taskId"] = taskId }));

    [Fact]
    public void NoSpec_StaysOpen()
    {
        var d = EpicCompletion.Evaluate(Epic(), new List<SpecRecord>(), new List<IssueRecord>());
        Assert.False(d.ShouldClose);
        Assert.Contains("no spec", d.Reason);
    }

    [Fact]
    public void SpecNotPastGrooming_StaysOpen()
    {
        var d = EpicCompletion.Evaluate(Epic(),
            new List<SpecRecord> { Spec("spec-1", "epic-1", SpecStatus.Approved) },
            new List<IssueRecord>());
        Assert.False(d.ShouldClose);
    }

    [Fact]
    public void TerminalTree_Closes()
    {
        var issues = new List<IssueRecord>
        {
            Issue("story-1", "story", "spec-1", IssueStatus.Completed),
            Issue("task-1", "task", "story-1", IssueStatus.Completed),
            Issue("task-2", "task", "story-1", IssueStatus.Closed),
        };
        var d = EpicCompletion.Evaluate(Epic(),
            new List<SpecRecord> { Spec("spec-1", "epic-1", SpecStatus.Groomed) }, issues);
        Assert.True(d.ShouldClose);
    }

    [Fact]
    public void OpenTask_StaysOpen()
    {
        var issues = new List<IssueRecord>
        {
            Issue("story-1", "story", "spec-1", IssueStatus.Completed),
            Issue("task-1", "task", "story-1", IssueStatus.InProgress),
        };
        var d = EpicCompletion.Evaluate(Epic(),
            new List<SpecRecord> { Spec("spec-1", "epic-1", SpecStatus.Groomed) }, issues);
        Assert.False(d.ShouldClose);
    }

    [Fact]
    public void FailedDescendant_StaysOpen_OperatorDecision()
    {
        var issues = new List<IssueRecord>
        {
            Issue("story-1", "story", "spec-1", IssueStatus.Completed),
            Issue("task-1", "task", "story-1", IssueStatus.Failed),
        };
        var d = EpicCompletion.Evaluate(Epic(),
            new List<SpecRecord> { Spec("spec-1", "epic-1", SpecStatus.Groomed) }, issues);
        Assert.False(d.ShouldClose);
        Assert.Contains("operator", d.Reason);
    }

    [Fact]
    public void LiveWatch_StaysOpen()
    {
        var issues = new List<IssueRecord>
        {
            Issue("story-1", "story", "spec-1", IssueStatus.Completed),
            Issue("task-1", "task", "story-1", IssueStatus.Completed),
            Watch("pr-watch-1", "task-1", IssueStatus.Pending),
        };
        var d = EpicCompletion.Evaluate(Epic(),
            new List<SpecRecord> { Spec("spec-1", "epic-1", SpecStatus.Groomed) }, issues);
        Assert.False(d.ShouldClose);
        Assert.Contains("watch", d.Reason);
    }

    [Fact]
    public void ShippedOrArchivedSpec_CountsAsTerminal()
    {
        var issues = new List<IssueRecord>
        {
            Issue("story-1", "story", "spec-1", IssueStatus.Closed),
        };
        var d = EpicCompletion.Evaluate(Epic(),
            new List<SpecRecord> { Spec("spec-1", "epic-1", SpecStatus.Shipped) }, issues);
        Assert.True(d.ShouldClose);
    }
}
