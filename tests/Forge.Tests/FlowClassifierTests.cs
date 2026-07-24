using Forge.Core;
using Forge.Dashboard.Flow;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// FlowGraph.ClassifyIssue: the pure mapping from (status,
/// checkpoint, metadata, sprint membership, spec chain) to the
/// flow node — every branch mirrors a pipeline branch. Plus the
/// journey builder's event-timeline derivation.
/// </summary>
public class FlowClassifierTests
{
    private static IssueRecord Issue(
        IssueStatus status,
        DispatchCheckpoint? ckpt = null,
        string? pr = null,
        string? groomed = null,
        string type = "task",
        string? parent = null)
        => new(
            Id: "task-1", ShortId: "1", Type: type, Title: "t",
            Description: null, Status: status, Priority: 2, Assignee: null,
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, ClosedAt: null,
            MetadataJson: (pr is not null || groomed is not null)
                ? System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["prNumber"] = pr!, ["groomed"] = groomed!,
                }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value))
                : "{}",
            ParentIssueId: parent, DispatchCheckpoint: ckpt);

    [Fact]
    public void PendingAdHocUngroomed_IsInGroomQueue()
        => Assert.Equal("groom", FlowGraph.ClassifyIssue(Issue(IssueStatus.Pending), inActiveSprint: false, hasSpecChain: false));

    [Fact]
    public void PendingAdHocGroomed_IsBacklog()
        => Assert.Equal("backlog", FlowGraph.ClassifyIssue(Issue(IssueStatus.Pending, groomed: "true"), false, false));

    [Fact]
    public void PendingSpecChain_IsBacklog_EvenWithoutMarker()
        => Assert.Equal("backlog", FlowGraph.ClassifyIssue(Issue(IssueStatus.Pending, parent: "story-1"), false, hasSpecChain: true));

    [Fact]
    public void PendingSprintMember_IsSprint()
        => Assert.Equal("sprint", FlowGraph.ClassifyIssue(Issue(IssueStatus.Pending, parent: "story-1"), inActiveSprint: true, hasSpecChain: true));

    [Fact]
    public void PendingWithPr_IsReworkQueue()
        => Assert.Equal("rework", FlowGraph.ClassifyIssue(Issue(IssueStatus.Pending, pr: "42"), false, true));

    [Fact]
    public void InProgressClaimedOnly_IsSetup()
        => Assert.Equal("setup", FlowGraph.ClassifyIssue(Issue(IssueStatus.InProgress, DispatchCheckpoint.Claimed), false, true));

    [Fact]
    public void InProgressWorktreeAcquired_IsAgentRun()
        => Assert.Equal("agent", FlowGraph.ClassifyIssue(Issue(IssueStatus.InProgress, DispatchCheckpoint.WorktreeAcquired), false, true));

    [Fact]
    public void InProgressPrOpened_IsReview()
        => Assert.Equal("review", FlowGraph.ClassifyIssue(Issue(IssueStatus.InProgress, DispatchCheckpoint.PrOpened, pr: "42"), false, true));

    [Fact]
    public void InProgressReworkRound_IsAgentNotReview()
        => Assert.Equal("agent", FlowGraph.ClassifyIssue(Issue(IssueStatus.InProgress, DispatchCheckpoint.WorktreeAcquired, pr: "42"), false, true));

    [Fact]
    public void Completed_IsDone_Failed_IsBlocked()
    {
        Assert.Equal("done", FlowGraph.ClassifyIssue(Issue(IssueStatus.Completed, pr: "42"), false, true));
        Assert.Equal("blocked", FlowGraph.ClassifyIssue(Issue(IssueStatus.Failed), false, true));
        Assert.Equal("blocked", FlowGraph.ClassifyIssue(Issue(IssueStatus.Blocked), false, true));
    }

    [Fact]
    public void WatchesAndContainers_AreHidden()
    {
        Assert.Null(FlowGraph.ClassifyIssue(Issue(IssueStatus.Pending, type: "pr-watch"), false, false));
        Assert.Null(FlowGraph.ClassifyIssue(Issue(IssueStatus.Pending, type: "story"), false, false));
        Assert.Null(FlowGraph.ClassifyIssue(Issue(IssueStatus.Pending, type: "epic"), false, false));
    }

    [Fact]
    public void Journey_FromTimeline_ToCurrent()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        var issue = Issue(IssueStatus.InProgress, DispatchCheckpoint.PrOpened, pr: "42", parent: "story-1");
        var events = new List<(string, DateTime, string?)>
        {
            ("created", t0, null),
            ("claimed", t0.AddMinutes(10), null),
            ("status_change", t0.AddMinutes(10), "Pending->InProgress"),
        };
        var visits = FlowGraph.BuildJourney(issue, events, "review");

        Assert.Equal(new[] { "backlog", "setup", "agent", "review" }, visits.Select(v => v.Node).ToArray());
        Assert.Equal("current", visits[^1].Note);
    }

    [Fact]
    public void Journey_ReworkLoop_CollapsesDuplicates()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        var issue = Issue(IssueStatus.Pending, pr: "42");
        var events = new List<(string, DateTime, string?)>
        {
            ("created", t0, null),
            ("status_change", t0.AddMinutes(5), "InProgress->Pending"),
            ("status_change", t0.AddMinutes(10), "InProgress->Pending"),
        };
        var visits = FlowGraph.BuildJourney(issue, events, "rework");

        Assert.Equal("groom", visits[0].Node);            // parentless → born in groom queue
        Assert.Equal("rework", visits[1].Node);
        Assert.Equal(2, visits[1].Count);                  // two requeues collapsed
        Assert.Equal("rework", visits[^1].Node);
    }
}
