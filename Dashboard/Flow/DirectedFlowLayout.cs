using Forge.Core.Workflow;

namespace Forge.Dashboard.Flow;

/// <summary>
/// Deterministic directed layout for the Flow page Live view —
/// replaces the dagre experiment (its automatic rank-centering
/// pushed the spine off-center and routed loop edges outside the
/// frame). Rules, not search:
///
/// <list type="bullet">
/// <item>The main path (intake → … → merge) is a CENTERED vertical
/// spine; consecutive spine edges are perfectly straight vertical
/// lines; forward skip edges (intake→groom, agent→done) bulge
/// gently left.</item>
/// <item>Non-spine nodes sit in fixed side columns — branch/loop
/// sinks right, failure sinks left — ranked just below their lowest
/// source.</item>
/// <item>Back-loops route on fixed outer margins (right column
/// outward, then up, then into the target's side); failure edges
/// mirror left. Nothing leaves the frame, by construction.</item>
/// </list>
///
/// Pure function over the resolved definition — no I/O. The UI
/// smooths the returned polylines (Catmull-Rom) into arcs.
/// </summary>
public static class DirectedFlowLayout
{
    public sealed record Point(double X, double Y);
    public sealed record LayoutNode(string Id, double X, double Y, double W, double H);
    public sealed record LayoutEdge(string From, string To, string Kind, string? Label, IReadOnlyList<Point> Points);
    public sealed record Result(
        double Width, double Height,
        IReadOnlyList<LayoutNode> Nodes,
        IReadOnlyList<LayoutEdge> Edges,
        double? LaneDividerY);

    private const double CanvasWidth = 940;
    private const double CenterX = 470;
    private const double LeftX = 240;
    private const double RightX = 700;
    private const double OuterRight = 830;   // back-loop margin (right of the right column)
    private const double OuterLeft = 110;    // failure-loop margin (left of the left column)
    private const double RankGap = 110;
    private const double TopMargin = 70;
    private const double NodeH = 46;

    public static Result Compute(WorkflowDefinition definition)
    {
        var steps = definition.Steps;
        var byId = steps.ToDictionary(s => s.Id, StringComparer.Ordinal);

        // Spine: walk from intake following stage-to-stage edges,
        // happy first (same rule as the editor's card list).
        var spine = new List<string>();
        var onSpine = new HashSet<string>(StringComparer.Ordinal);
        string? cur = byId.ContainsKey("intake") ? "intake" : steps.FirstOrDefault(s => s.Kind == "stage")?.Id;
        while (cur is not null && onSpine.Add(cur))
        {
            spine.Add(cur);
            cur = definition.Edges
                .Where(e => e.From == cur && byId.TryGetValue(e.To, out var t) && t.Kind == "stage" && !onSpine.Contains(e.To))
                .OrderBy(e => e.Kind == WorkflowEdgeKinds.Happy ? 0 : 1)
                .Select(e => (string?)e.To)
                .FirstOrDefault();
        }
        foreach (var s in steps.Where(s => s.Kind == "stage" && !onSpine.Contains(s.Id)))
        {
            spine.Add(s.Id);
            onSpine.Add(s.Id);
        }
        var spineRank = spine.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => (double)x.i, StringComparer.Ordinal);

        // Non-spine: side by incoming edge kind (failure → left, else
        // right); rank just below the lowest source, iterated so
        // chained sinks (parked → rework → blocked) order correctly.
        var side = new Dictionary<string, string>(StringComparer.Ordinal);
        var rank = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in steps.Where(s => !onSpine.Contains(s.Id)))
        {
            var incoming = definition.Edges.Where(e => e.To == s.Id).ToList();
            side[s.Id] = incoming.Any(e => e.Kind == WorkflowEdgeKinds.Failure) ? "left" : "right";
            rank[s.Id] = incoming
                .Select(e => spineRank.TryGetValue(e.From, out var r) ? r : (double?)null)
                .Where(r => r is not null)
                .Select(r => r!.Value)
                .DefaultIfEmpty(0)
                .Max() + 0.5;
        }
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var s in steps.Where(s => !onSpine.Contains(s.Id)))
            {
                foreach (var e in definition.Edges.Where(e => e.To == s.Id))
                {
                    var src = spineRank.TryGetValue(e.From, out var sr) ? sr
                        : rank.TryGetValue(e.From, out var nr) ? nr : (double?)null;
                    if (src is not null && rank[s.Id] < src.Value + 0.5)
                    {
                        rank[s.Id] = src.Value + 0.5;
                    }
                }
            }
        }

        double Y(double r) => TopMargin + r * RankGap;
        static double W(string label) => Math.Clamp(label.Length * 7.5 + 36, 110, 220);

        var nodes = new List<LayoutNode>();
        var pos = new Dictionary<string, LayoutNode>(StringComparer.Ordinal);
        foreach (var s in steps)
        {
            var node = onSpine.Contains(s.Id)
                ? new LayoutNode(s.Id, CenterX, Y(spineRank[s.Id]), W(s.Label), NodeH)
                : new LayoutNode(s.Id, side[s.Id] == "left" ? LeftX : RightX, Y(rank[s.Id]), W(s.Label), NodeH);
            nodes.Add(node);
            pos[s.Id] = node;
        }

        var edges = new List<LayoutEdge>();
        foreach (var e in definition.Edges)
        {
            if (!pos.TryGetValue(e.From, out var s) || !pos.TryGetValue(e.To, out var t))
            {
                continue;
            }
            edges.Add(new LayoutEdge(e.From, e.To, e.Kind, e.Label, Route(s, t)));
        }

        // Canvas: bottom of the lowest node + margin. Lane divider
        // between the planning and implementation ranks.
        var height = nodes.Max(n => n.Y + n.H / 2) + 46;
        var planning = nodes.Where(n => byId[n.Id].Lane == WorkflowLanes.Planning).ToList();
        var impl = nodes.Where(n => byId[n.Id].Lane == WorkflowLanes.Implementation).ToList();
        double? divider = planning.Count > 0 && impl.Count > 0
            ? (planning.Max(n => n.Y + n.H / 2) + impl.Min(n => n.Y - n.H / 2)) / 2
            : null;
        return new Result(CanvasWidth, height, nodes, edges, divider);
    }

    private static IReadOnlyList<Point> Route(LayoutNode s, LayoutNode t)
    {
        var sBottom = s.Y + s.H / 2;
        var sTop = s.Y - s.H / 2;
        var tTop = t.Y - t.H / 2;
        var tBottom = t.Y + t.H / 2;
        var sRight = s.X + s.W / 2;
        var sLeft = s.X - s.W / 2;
        var tRight = t.X + t.W / 2;
        var tLeft = t.X - t.W / 2;

        // Same column (spine or a side column): straight vertical.
        if (Math.Abs(s.X - t.X) < 1)
        {
            return t.Y > s.Y
                ? new[] { new Point(s.X, sBottom), new Point(t.X, tTop) }
                : new[] { new Point(s.X, sTop), new Point(t.X, tBottom) };
        }

        // Both on the spine, forward skip (intake→groom, agent→done):
        // gentle bulge LEFT, clear of the spine's node boxes.
        var sIsSpine = Math.Abs(s.X - CenterX) < 1;
        var tIsSpine = Math.Abs(t.X - CenterX) < 1;
        if (sIsSpine && tIsSpine && t.Y > s.Y)
        {
            var midY = (sBottom + tTop) / 2;
            return new[] { new Point(s.X, sBottom), new Point(s.X - 150, midY), new Point(t.X, tTop) };
        }

        // Back-loop to the spine (rework → agent): out to the fixed
        // outer margin, up, into the target's side.
        if (tIsSpine && t.Y < s.Y)
        {
            var (exitX, entryX, margin) = s.X >= CenterX
                ? (sRight, tRight, OuterRight)
                : (sLeft, tLeft, OuterLeft);
            return new[]
            {
                new Point(exitX, s.Y),
                new Point(margin, s.Y),
                new Point(margin, t.Y),
                new Point(entryX, t.Y),
            };
        }

        // Downward edge to a side column: elbow — out the source's
        // side, across at the source's rank, down into the target's top.
        if (t.Y > s.Y)
        {
            // Far-side crossing (rework → blocked): route UNDER the
            // bottom rank and enter from below, never through nodes.
            var crossesSpine = (s.X - CenterX) * (t.X - CenterX) < 0;
            if (crossesSpine)
            {
                var underY = Math.Max(sBottom, tBottom) + 28;
                return new[]
                {
                    new Point(s.X, sBottom),
                    new Point(s.X, underY),
                    new Point(t.X, underY),
                    new Point(t.X, tBottom),
                };
            }
            var exit = t.X > s.X ? sRight : sLeft;
            return new[]
            {
                new Point(exit, s.Y),
                new Point(t.X, s.Y),
                new Point(t.X, tTop),
            };
        }

        // Upward edge between side columns (defensive — not in the
        // default graph): outer margin on the source's side.
        var (ex, en, m) = s.X >= CenterX ? (sRight, tRight, OuterRight) : (sLeft, tLeft, OuterLeft);
        return new[]
        {
            new Point(ex, s.Y),
            new Point(m, s.Y),
            new Point(m, t.Y),
            new Point(t.X > m ? tRight : tLeft, t.Y),
        };
    }
}
