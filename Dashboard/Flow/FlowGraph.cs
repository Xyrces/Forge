using Forge.Core;

namespace Forge.Dashboard.Flow;

/// <summary>
/// The pipeline flow graph: a declarative, fixed-layout DAG of the
/// stages an issue passes through, split into two lanes —
/// <b>planning</b> (intake → design → groom → backlog → sprint;
/// fed by specs + ad-hoc tasks) and <b>implementation</b>
/// (setup → agent → PR → review → rework → merge; fed by tasks).
///
/// <para>
/// Everything here is DERIVED from existing stores (issue rows,
/// spec rows, sprint memberships, watch metadata, issue_event
/// timelines). No new tables, no new writes — the flow view cannot
/// bloat the datastore, by construction.
/// </para>
/// </summary>
public static class FlowGraph
{
    public const string LanePlanning = "planning";
    public const string LaneImplementation = "implementation";

    public sealed record Node(string Id, string Label, string Lane, int X, int Y);
    public sealed record Edge(string From, string To);

    public static readonly IReadOnlyList<Node> Nodes = new Node[]
    {
        // Planning lane (left → right).
        new("intake",  "Intake",        LanePlanning, 60,  90),
        new("design",  "Design",        LanePlanning, 210, 90),
        new("groom",   "Groom",         LanePlanning, 360, 90),
        new("backlog", "Groomed backlog", LanePlanning, 510, 90),
        new("sprint",  "Sprint",        LanePlanning, 660, 90),
        // Implementation lane (snakes right → left under planning).
        new("setup",   "Claim + worktree", LaneImplementation, 660, 290),
        new("agent",   "Agent run",     LaneImplementation, 510, 290),
        new("pr",      "PR open",       LaneImplementation, 360, 290),
        new("review",  "Review + CI",   LaneImplementation, 210, 290),
        new("done",    "Merged / done", LaneImplementation, 60,  290),
        // Loop + failure sinks below the implementation lane.
        new("rework",  "Rework loop",   LaneImplementation, 360, 420),
        new("blocked", "Blocked / failed", LaneImplementation, 60, 420),
    };

    public static readonly IReadOnlyList<Edge> Edges = new Edge[]
    {
        new("intake", "design"),   // send-to-designer (visual)
        new("intake", "groom"),    // operator approve (non-visual fast path)
        new("design", "groom"),    // designed → groomable
        new("groom", "backlog"),   // spec groomed / ad-hoc approved
        new("backlog", "sprint"),  // assembler ingests
        new("sprint", "setup"),    // dispatch claims
        new("setup", "agent"),     // worktree ready → run
        new("agent", "pr"),        // diff → commit/push/PR
        new("agent", "done"),      // verified NO_CHANGES_NEEDED
        new("agent", "blocked"),   // no-progress breaker / unrecoverable
        new("pr", "review"),       // watcher picks up
        new("review", "done"),     // CI green + approval → merge
        new("review", "rework"),   // CI fail / changes requested
        new("rework", "agent"),    // redispatch, same branch/PR
        new("rework", "blocked"),  // rework circuit breaker (3)
    };

    public static Node? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>
    /// Classify a spec into its planning-lane node, or null when the
    /// spec has left planning (Groomed and beyond — its tasks carry
    /// the flow from there).
    /// </summary>
    public static string? ClassifySpec(SpecStatus status) => status switch
    {
        SpecStatus.Draft => "intake",
        SpecStatus.ReadyForDesign => "design",
        SpecStatus.Designed => "groom",
        SpecStatus.AssetReady => "groom",
        SpecStatus.Approved => "groom",
        SpecStatus.Grooming => "groom",
        SpecStatus.NeedsRevision => "intake",   // bounced back to the operator
        _ => null,
    };

    /// <summary>
    /// Classify an issue (task) into its current node. Pure function
    /// of the row + metadata + sprint membership; every branch here
    /// mirrors a branch in the pipeline itself.
    /// </summary>
    /// <param name="issue">The issue row.</param>
    /// <param name="inActiveSprint">Whether the issue is linked to the ACTIVE sprint.</param>
    /// <param name="hasSpecChain">Whether the issue's parent chain reaches a spec (groomed with it).</param>
    public static string? ClassifyIssue(IssueRecord issue, bool inActiveSprint, bool hasSpecChain)
    {
        // Plumbing, not work: watches drive the review node but are
        // not themselves shown; containers are planning units whose
        // place is carried by the spec.
        if (issue.Type == AgentTaskTypes.PrWatch || AgentTaskTypes.IsContainer(issue.Type))
        {
            return null;
        }

        var prNumber = issue.GetMetadata("prNumber");

        switch (issue.Status)
        {
            case IssueStatus.Completed:
            case IssueStatus.Closed:
                return "done";
            case IssueStatus.Failed:
            case IssueStatus.Blocked:
                return "blocked";
            case IssueStatus.Pending:
                if (prNumber is not null) return "rework";           // queued rework round
                if (inActiveSprint) return "sprint";
                if (hasSpecChain) return "backlog";                  // groomed with its spec
                return string.Equals(issue.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase)
                    ? "backlog"                                       // ad-hoc, groom-approved
                    : "groom";                                        // ad-hoc, awaiting grooming
            case IssueStatus.InProgress:
                // InProgress + prNumber + PrOpened = waiting on review/CI.
                // InProgress + prNumber + earlier checkpoint = rework round running.
                if (issue.DispatchCheckpoint >= DispatchCheckpoint.PrOpened) return "review";
                if (issue.DispatchCheckpoint >= DispatchCheckpoint.WorktreeAcquired) return "agent";
                return "setup";
            default:
                return null;
        }
    }

    /// <summary>
    /// Rebuild an issue's journey through the graph from its
    /// issue_event timeline, terminated by its current classified
    /// node. Visits are chronological; consecutive duplicates
    /// (requeue loops) collapse into one visit with a count.
    /// </summary>
    public sealed record JourneyVisit(string Node, DateTime At, string? Note, int Count);

    public static IReadOnlyList<JourneyVisit> BuildJourney(
        IssueRecord issue,
        IReadOnlyList<(string Kind, DateTime At, string? Detail)> events,
        string? currentNode)
    {
        var visits = new List<JourneyVisit>();
        void Push(string node, DateTime at, string? note)
        {
            if (visits.Count > 0 && visits[^1].Node == node)
            {
                var last = visits[^1];
                visits[^1] = last with { Count = last.Count + 1, At = at, Note = note ?? last.Note };
                return;
            }
            visits.Add(new JourneyVisit(node, at, note, 1));
        }

        var prNumber = issue.GetMetadata("prNumber");
        var adHoc = issue.ParentIssueId is null;
        var inRun = false;   // between claim and exit-from-InProgress
        var agentStarted = false;

        foreach (var (kind, at, detail) in events)
        {
            switch (kind)
            {
                case "created":
                    // Spec-chain tasks are born in the groomer's output;
                    // ad-hoc tasks enter the groom queue.
                    Push(adHoc ? "groom" : "backlog", at, adHoc ? "filed (awaits grooming)" : "created by groomer");
                    break;
                case "claimed":
                    Push("setup", at, "claimed by dispatcher");
                    inRun = true;
                    agentStarted = false;
                    break;
                case "status_change" when detail is not null:
                {
                    if (detail.Contains("InProgress->InProgress", StringComparison.Ordinal))
                    {
                        // Checkpoint write. The FIRST one after a claim
                        // is worktree-acquired ≈ agent run start; later
                        // ones (commit/push/PR checkpoints) are noise
                        // for stage timing. This is what makes the
                        // agent node's duration the true run wall-time
                        // (was: agent stamped at claim → 0s runs).
                        if (inRun && !agentStarted)
                        {
                            Push("agent", at, null);
                            agentStarted = true;
                        }
                    }
                    else if (detail.Contains("Pending->InProgress", StringComparison.Ordinal))
                    {
                        // The claim transition itself (fires with the
                        // claimed event) — not a stage of its own.
                    }
                    else if (detail.Contains("InProgress->Pending", StringComparison.Ordinal))
                    {
                        Push(prNumber is not null ? "rework" : "sprint", at,
                            detail.Contains("llm-429", StringComparison.Ordinal) ? "LLM 429 requeue"
                            : detail.Contains("no diff", StringComparison.Ordinal) ? "no-progress requeue"
                            : prNumber is not null ? "rework queued" : "requeued");
                        inRun = false;
                    }
                    else if (detail.Contains("->InProgress", StringComparison.Ordinal))
                    {
                        // Any other entry into InProgress (e.g. recovery
                        // replay): treat as a run starting.
                        Push("agent", at, null);
                        inRun = true;
                        agentStarted = true;
                    }
                    else if (detail.Contains("->Completed", StringComparison.Ordinal))
                    {
                        Push("done", at, prNumber is not null ? "merged" : "no-op");
                        inRun = false;
                    }
                    else if (detail.Contains("->Closed", StringComparison.Ordinal))
                    {
                        Push("done", at, "closed");
                        inRun = false;
                    }
                    else if (detail.Contains("->Failed", StringComparison.Ordinal)
                        || detail.Contains("->Blocked", StringComparison.Ordinal))
                    {
                        Push("blocked", at, detail);
                        inRun = false;
                    }
                    break;
                }
            }
        }

        // Terminal append: where the issue sits NOW (if the timeline
        // didn't already end there).
        if (currentNode is not null && (visits.Count == 0 || visits[^1].Node != currentNode))
        {
            Push(currentNode, issue.UpdatedAt, "current");
        }
        return visits;
    }
}
