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
    // Nodes carrying a gate note (plan gate / pre-push verify /
    // CI+approval gate) grow one caption line taller.
    private const double NoteExtraH = 18;

    public static Result Compute(WorkflowDefinition definition, IReadOnlyDictionary<string, string>? nodeNotes = null)
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
            var h = nodeNotes is not null && nodeNotes.ContainsKey(s.Id) ? NodeH + NoteExtraH : NodeH;
            var node = onSpine.Contains(s.Id)
                ? new LayoutNode(s.Id, CenterX, Y(spineRank[s.Id]), W(s.Label), h)
                : new LayoutNode(s.Id, side[s.Id] == "left" ? LeftX : RightX, Y(rank[s.Id]), W(s.Label), h);
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

    // ---- Horizontal compact variant (dashboard strip) ----

    private const double HLeftMargin = 90;
    private const double HRankGap = 150;
    private const double HCenterY = 200;
    private const double HSinkGap = 130;      // sink rows above/below the spine
    private const double HOuterBottom = 393;  // back-loop margin under the sink row
    private const double HTopMargin = 70;     // failure row

    /// <summary>
    /// Horizontal variant for the dashboard's compact strip: the
    /// spine runs left → right on the center row with straight
    /// horizontal forward edges; branch/loop sinks sit BELOW,
    /// failure sinks ABOVE; back-loops route under the sink row and
    /// cross-canvas edges around the right end. Same rules, axes
    /// transposed.
    /// </summary>
    public static Result ComputeHorizontal(WorkflowDefinition definition)
    {
        var (spineRank, side, rank, byId) = Analyze(definition);
        var onSpine = spineRank.Keys.ToHashSet(StringComparer.Ordinal);

        double X(double r) => HLeftMargin + r * HRankGap;
        static double W(string label) => Math.Clamp(label.Length * 7.5 + 36, 110, 220);

        var nodes = new List<LayoutNode>();
        var pos = new Dictionary<string, LayoutNode>(StringComparer.Ordinal);
        foreach (var s in byId.Values)
        {
            var node = onSpine.Contains(s.Id)
                ? new LayoutNode(s.Id, X(spineRank[s.Id]), HCenterY, W(s.Label), NodeH)
                : new LayoutNode(s.Id, X(rank[s.Id]),
                    side[s.Id] == "left" ? HCenterY - HSinkGap : HCenterY + HSinkGap,
                    W(s.Label), NodeH);
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
            edges.Add(new LayoutEdge(e.From, e.To, e.Kind, e.Label, RouteHorizontal(s, t)));
        }

        var width = nodes.Max(n => n.X + n.W / 2) + 100;   // room for the right-end wrap
        var height = HOuterBottom + 47;
        var planning = nodes.Where(n => byId[n.Id].Lane == WorkflowLanes.Planning).ToList();
        var impl = nodes.Where(n => byId[n.Id].Lane == WorkflowLanes.Implementation).ToList();
        double? divider = planning.Count > 0 && impl.Count > 0
            ? (planning.Max(n => n.X + n.W / 2) + impl.Min(n => n.X - n.W / 2)) / 2
            : null;
        return new Result(width, height, nodes, edges, divider);
    }

    private static IReadOnlyList<Point> RouteHorizontal(LayoutNode s, LayoutNode t)
    {
        var sRight = s.X + s.W / 2;
        var sLeft = s.X - s.W / 2;
        var sTop = s.Y - s.H / 2;
        var sBottom = s.Y + s.H / 2;
        var tRight = t.X + t.W / 2;
        var tLeft = t.X - t.W / 2;
        var tTop = t.Y - t.H / 2;
        var tBottom = t.Y + t.H / 2;

        // Same row (spine-consecutive, or sink-row chains like
        // parked → rework): straight horizontal.
        if (Math.Abs(s.Y - t.Y) < 1)
        {
            return t.X > s.X
                ? new[] { new Point(sRight, s.Y), new Point(tLeft, t.Y) }
                : new[] { new Point(sLeft, s.Y), new Point(tRight, t.Y) };
        }

        // Same column (e.g. pr → parked): straight vertical.
        if (Math.Abs(s.X - t.X) < 1)
        {
            return t.Y > s.Y
                ? new[] { new Point(s.X, sBottom), new Point(t.X, tTop) }
                : new[] { new Point(s.X, sTop), new Point(t.X, tBottom) };
        }

        var sOnSpine = Math.Abs(s.Y - HCenterY) < 1;
        var tOnSpine = Math.Abs(t.Y - HCenterY) < 1;

        // Both on the spine, forward skip (intake→groom, agent→done):
        // gentle bulge ABOVE, clear of the spine's node boxes.
        if (sOnSpine && tOnSpine && t.X > s.X)
        {
            var midX = (sRight + tLeft) / 2;
            return new[] { new Point(sRight, s.Y), new Point(midX, s.Y - 70), new Point(tLeft, t.Y) };
        }

        // Back-loop to the spine (rework → agent): down to the fixed
        // bottom margin, across, up into the target's bottom.
        if (tOnSpine && t.X < s.X)
        {
            return new[]
            {
                new Point(s.X, sBottom),
                new Point(s.X, HOuterBottom),
                new Point(t.X, HOuterBottom),
                new Point(t.X, tBottom),
            };
        }

        // Opposite sides of the spine (rework → blocked): wrap
        // around the right end.
        if ((s.Y - HCenterY) * (t.Y - HCenterY) < 0 && !tOnSpine && !sOnSpine)
        {
            var wrapX = Math.Max(sRight, tRight) + 60;
            return new[]
            {
                new Point(sRight, s.Y),
                new Point(wrapX, s.Y),
                new Point(wrapX, t.Y),
                new Point(tRight, t.Y),
            };
        }

        // Forward edge between spine and a sink row.
        if (t.X >= s.X)
        {
            // Failure UP to the top row, or branch DOWN to the bottom
            // row: elbow — out the source's top/bottom at its column,
            // across at the target's row, into the target's side.
            var exitY = t.Y < s.Y ? sTop : sBottom;
            return new[]
            {
                new Point(s.X, exitY),
                new Point(s.X, t.Y),
                new Point(tLeft, t.Y),
            };
        }

        // Cross-canvas (rework below → blocked above): around the
        // right end, never through the spine's boxes.
        var outerX = Math.Max(sRight, tRight) + 60;
        return new[]
        {
            new Point(sRight, s.Y),
            new Point(outerX, s.Y),
            new Point(outerX, t.Y),
            new Point(tRight, t.Y),
        };
    }

    // ---- Shared graph analysis (spine, sink sides, sink ranks) ----

    private static (Dictionary<string, double> SpineRank, Dictionary<string, string> Side, Dictionary<string, double> Rank, Dictionary<string, WorkflowStep> ById)
        Analyze(WorkflowDefinition definition)
    {
        var steps = definition.Steps;
        var byId = steps.ToDictionary(s => s.Id, StringComparer.Ordinal);

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
        return (spineRank, side, rank, byId);
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
