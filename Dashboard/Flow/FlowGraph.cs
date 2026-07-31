using Forge.Core;

namespace Forge.Dashboard.Flow;

/// <summary>
/// Classification + journey logic for the pipeline flow view: maps
/// specs and issues onto the step ids of the resolved workflow
/// definition (<see cref="Forge.Core.Workflow.WorkflowDefaults"/>),
/// and rebuilds per-issue journeys from the issue_event timeline.
///
/// <para>
/// The graph SHAPE (steps/edges/layout) lives in the workflow
/// definition, resolved per request — this file only knows how to
/// place reality onto its step ids. Everything here is DERIVED from
/// existing stores (issue rows, spec rows, sprint memberships,
/// watch metadata, issue_event timelines). No new tables, no new
/// writes — the flow view cannot bloat the datastore, by
/// construction.
/// </para>
/// </summary>
public static class FlowGraph
{
    /// <summary>
    /// Classify a spec into its planning-lane node, or null when the
    /// spec has left planning (Groomed and beyond — its tasks carry
    /// the flow from there). When the design step is disabled in the
    /// workflow definition (pass 4), a ReadyForDesign spec has
    /// nowhere to go — it classifies back to intake, where the
    /// operator's fast path (approve) lives.
    /// </summary>
    public static string? ClassifySpec(SpecStatus status, bool designEnabled = true) => status switch
    {
        SpecStatus.Draft => "intake",
        SpecStatus.ReadyForDesign => designEnabled ? "design" : "intake",
        SpecStatus.Designed => "groom",
        SpecStatus.AssetReady => "groom",
        SpecStatus.Approved => "groom",
        SpecStatus.Grooming => "groom",
        SpecStatus.NeedsRevision => "intake",   // bounced back to the operator
        _ => null,
    };

    /// <summary>
    /// Classify an issue (task) into its current node. The machine's
    /// recorded lifecycle state (metadata.state) is authoritative;
    /// the status/metadata heuristic below is the fallback for
    /// entities that predate the machine.
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

        var recorded = issue.GetMetadata("state");
        if (recorded is not null)
        {
            return recorded switch
            {
                "Pending" => ClassifyPendingPlanning(issue, inActiveSprint, hasSpecChain),
                "Dispatching" => "setup",
                "AgentRunning" => "agent",
                "ReworkQueued" or "ReworkRunning" or "StalledRework" => "rework",
                "PROpen" => "pr",
                "MergeReady" => "review",
                "ParkedInfra" => "parked",
                "Merged" or "Completed" or "Closed" => "done",
                "Failed" or "BlockedOperator" => "blocked",
                _ => null,
            };
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
                return ClassifyPendingPlanning(issue, inActiveSprint, hasSpecChain);
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

    private static string ClassifyPendingPlanning(IssueRecord issue, bool inActiveSprint, bool hasSpecChain)
    {
        if (issue.GetMetadata("prNumber") is not null) return "rework";   // queued rework round
        if (inActiveSprint) return "sprint";
        if (hasSpecChain) return "backlog";                               // groomed with its spec
        return string.Equals(issue.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase)
            ? "backlog"                                                   // ad-hoc, groom-approved
            : "groom";                                                    // ad-hoc, awaiting grooming
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
                            : detail.Contains("llm-auth", StringComparison.Ordinal) ? "LLM auth requeue (provider credentials)"
                            : detail.Contains("no diff", StringComparison.Ordinal) ? "no-progress requeue"
                            : detail.Contains("pre-push verification failed", StringComparison.OrdinalIgnoreCase) ? "pre-push verification failed"
                            : prNumber is not null ? ReworkNote(issue) : "requeued");
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

    /// <summary>Journey note for a rework requeue: names the round
    /// and the cause from the task's metadata (the transition detail
    /// itself carries no error for rework rounds), and flags the
    /// warm-session resume when a persisted session exists.</summary>
    private static string ReworkNote(IssueRecord issue)
    {
        var reason = issue.GetMetadata("reworkReason") ?? "";
        var round = int.TryParse(issue.GetMetadata("reviewRound"), out var rr) ? rr : 0;
        var note = reason.Contains("reviewer requested changes", StringComparison.OrdinalIgnoreCase)
            ? $"review requested changes{(round > 0 ? $" (round {round})" : "")}"
            : reason.Length > 0 ? reason : "rework queued";
        return !string.IsNullOrEmpty(issue.GetMetadata("agentSessionId"))
            ? note + " — resumes warm session"
            : note;
    }
}
