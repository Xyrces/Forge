using Forge.Core.Workflow;
using Forge.Dashboard.Flow;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// The deterministic directed layout: centered spine, straight
/// forward edges, side columns for sinks, framed loop routing.
/// These assertions are the operator's design requirements.
/// </summary>
public sealed class DirectedFlowLayoutTests
{
    private static readonly string[] SpineIds =
        { "intake", "design", "groom", "backlog", "sprint", "setup", "agent", "pr", "review", "done" };

    [Fact]
    public void Spine_IsCentered_AndOrdered()
    {
        var r = DirectedFlowLayout.Compute(WorkflowDefaults.Definition);
        var spine = r.Nodes.Where(n => SpineIds.Contains(n.Id)).ToList();
        Assert.All(spine, n => Assert.Equal(470, n.X));
        var byId = r.Nodes.ToDictionary(n => n.Id);
        for (var i = 1; i < SpineIds.Length; i++)
        {
            Assert.True(byId[SpineIds[i]].Y > byId[SpineIds[i - 1]].Y,
                $"{SpineIds[i]} should rank below {SpineIds[i - 1]}");
        }
    }

    [Fact]
    public void ConsecutiveSpineEdges_AreStraightVerticalLines()
    {
        var r = DirectedFlowLayout.Compute(WorkflowDefaults.Definition);
        var consecutive = new[]
        {
            ("intake", "design"), ("design", "groom"), ("groom", "backlog"),
            ("backlog", "sprint"), ("sprint", "setup"), ("setup", "agent"),
            ("agent", "pr"), ("pr", "review"), ("review", "done"),
        };
        foreach (var (from, to) in consecutive)
        {
            var e = r.Edges.Single(e => e.From == from && e.To == to);
            Assert.Equal(2, e.Points.Count);
            Assert.Equal(e.Points[0].X, e.Points[1].X);   // perfectly vertical
            Assert.Equal(470, e.Points[0].X);             // on the centered spine
        }
    }

    [Fact]
    public void Sinks_SitInSideColumns_BelowTheirSources()
    {
        var r = DirectedFlowLayout.Compute(WorkflowDefaults.Definition);
        var byId = r.Nodes.ToDictionary(n => n.Id);
        Assert.Equal(700, byId["parked"].X);              // branch sink: right column
        Assert.Equal(700, byId["rework"].X);
        Assert.Equal(240, byId["blocked"].X);             // failure sink: left column
        Assert.True(byId["parked"].Y > byId["pr"].Y);
        Assert.True(byId["rework"].Y > byId["review"].Y);
        Assert.True(byId["rework"].Y > byId["parked"].Y); // chained sinks order
        Assert.True(byId["blocked"].Y >= byId["rework"].Y);
    }

    [Fact]
    public void BackLoop_RoutesOnTheOuterMargin_NotThroughNodes()
    {
        var r = DirectedFlowLayout.Compute(WorkflowDefaults.Definition);
        var loop = r.Edges.Single(e => e is { From: "rework", To: "agent" });
        // Out to the fixed right margin, up, into the agent's side.
        Assert.Contains(loop.Points, p => p.X == 830);
        var byId = r.Nodes.ToDictionary(n => n.Id);
        Assert.All(loop.Points, p => Assert.True(p.X > byId["parked"].X + byId["parked"].W / 2
            || p.Y == byId["agent"].Y || p.Y == byId["rework"].Y,
            $"loop point ({p.X},{p.Y}) must not cross the right column"));
    }

    [Fact]
    public void Everything_StaysInsideTheFrame()
    {
        var r = DirectedFlowLayout.Compute(WorkflowDefaults.Definition);
        foreach (var n in r.Nodes)
        {
            Assert.True(n.X - n.W / 2 >= 0, $"{n.Id} hangs off the left edge");
            Assert.True(n.X + n.W / 2 <= r.Width, $"{n.Id} hangs off the right edge");
            Assert.True(n.Y - n.H / 2 >= 0 && n.Y + n.H / 2 <= r.Height, $"{n.Id} hangs off the top/bottom");
        }
        foreach (var e in r.Edges)
        {
            foreach (var p in e.Points)
            {
                Assert.True(p.X >= 0 && p.X <= r.Width, $"{e.From}->{e.To} point x={p.X} outside [0,{r.Width}]");
                Assert.True(p.Y >= 0 && p.Y <= r.Height, $"{e.From}->{e.To} point y={p.Y} outside [0,{r.Height}]");
            }
        }
    }

    [Fact]
    public void LaneDivider_SitsBetweenPlanningAndImplementation()
    {
        var r = DirectedFlowLayout.Compute(WorkflowDefaults.Definition);
        Assert.NotNull(r.LaneDividerY);
        var byId = r.Nodes.ToDictionary(n => n.Id);
        Assert.True(r.LaneDividerY > byId["sprint"].Y);
        Assert.True(r.LaneDividerY < byId["setup"].Y);
    }
}
