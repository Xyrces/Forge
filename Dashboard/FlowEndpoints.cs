using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Forge.Core;
using Forge.Core.Workflow;
using Forge.Dashboard.Flow;
using Forge.Orchestrator.Sprint;

namespace Forge.Dashboard;

/// <summary>
/// Flow observability: a node-based, flowchart view of issues
/// moving through the pipeline (planning lane: specs + ad-hoc
/// tasks; implementation lane: tasks). 100% derived from existing
/// stores — no new tables, no new writes, no datastore growth.
/// Near real-time: the UI polls every few seconds.
/// </summary>
public static class FlowEndpoints
{
    private const int MaxSamplesPerNode = 8;

    public static void MapFlowEndpoints(
        WebApplication app,
        IIssueStore issues,
        ISpecStore specs,
        ISprintStore sprints,
        Orchestrator.MemoryExtractionStore? extractions = null,
        WorkflowResolver? workflow = null)
    {
        app.MapGet("/api/flow", async (CancellationToken ct) =>
        {
            // The graph shape comes from the RESOLVED workflow
            // definition (published override → built-in default);
            // classification maps reality onto its step ids.
            var definition = workflow is not null
                ? await workflow.ResolveAsync(ct)
                : WorkflowDefaults.Definition;
            var counts = definition.Steps.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
            var samples = definition.Steps.ToDictionary(
                n => n.Id, _ => new List<object>(MaxSamplesPerNode), StringComparer.Ordinal);
            void Add(string nodeId, string id, string title, string status)
            {
                counts[nodeId]++;
                if (samples[nodeId].Count < MaxSamplesPerNode)
                {
                    samples[nodeId].Add(new { id, title, status });
                }
            }

            // Planning lane: specs still planning. Classification
            // honors disabled steps (pass 4).
            var designEnabled = definition.IsStepEnabled("design");
            foreach (var spec in await specs.ListAsync(projectId: null, status: null, ct))
            {
                var node = FlowGraph.ClassifySpec(spec.Status, designEnabled);
                if (node is not null) Add(node, spec.Id, spec.Title, spec.Status.ToString());
            }

            // Implementation lane + ad-hoc planning: tasks.
            var all = (await issues.ListAsync(new IssueFilter(), ct)).ToList();
            var byId = all.ToDictionary(i => i.Id);
            var active = await sprints.GetActiveAsync(ct);
            var sprintMembers = active is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(await sprints.GetIssueIdsAsync(active.Id, ct), StringComparer.Ordinal);

            foreach (var issue in all)
            {
                var hasSpecChain = SprintAssembler.ResolveGroupKey(issue, byId) != SprintAssembler.AdHocGroupName;
                var node = FlowGraph.ClassifyIssue(issue, sprintMembers.Contains(issue.Id), hasSpecChain);
                if (node is not null) Add(node, issue.Id, issue.Title, issue.Status.ToString());
            }

            return Results.Json(new
            {
                lanes = new[] { WorkflowLanes.Planning, WorkflowLanes.Implementation },
                nodes = definition.Steps.Select(n => new
                {
                    id = n.Id,
                    label = n.Label,
                    lane = n.Lane,
                    x = n.X,
                    y = n.Y,
                    count = counts[n.Id],
                    issues = samples[n.Id],
                }),
                edges = definition.Edges.Select(e => new { from = e.From, to = e.To, kind = e.Kind, label = e.Label }),
                activeSprintId = active?.Id,
                // Issue-first picker: every traceable work item (newest
                // activity first), capped for payload size.
                allIssues = all
                    .Where(i => i.Type != AgentTaskTypes.PrWatch && !AgentTaskTypes.IsContainer(i.Type))
                    .OrderByDescending(i => i.UpdatedAt)
                    .Take(300)
                    .Select(i => new { id = i.Id, title = i.Title, status = i.Status.ToString() }),
            });
        });

        app.MapGet("/api/flow/issues/{id}", async (string id, CancellationToken ct) =>
        {
            var issue = await issues.GetAsync(id, ct);
            if (issue is null) return Results.NotFound();

            var all = (await issues.ListAsync(new IssueFilter(), ct)).ToList();
            var byId = all.ToDictionary(i => i.Id);
            var active = await sprints.GetActiveAsync(ct);
            var sprintMembers = active is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(await sprints.GetIssueIdsAsync(active.Id, ct), StringComparer.Ordinal);
            var hasSpecChain = SprintAssembler.ResolveGroupKey(issue, byId) != SprintAssembler.AdHocGroupName;
            var currentNode = FlowGraph.ClassifyIssue(issue, sprintMembers.Contains(issue.Id), hasSpecChain);

            var events = await issues.ListEventsAsync(id, limit: 200, ct);
            var journey = FlowGraph.BuildJourney(
                issue,
                // Store returns newest-first; the journey is chronological.
                events.OrderBy(e => e.Timestamp)
                    .Select(e => (e.Kind, e.Timestamp, (string?)e.Detail)).ToList(),
                currentNode);

            // Per-stage drill-down details. Everything here is read
            // from stores the pipeline already writes — the journey
            // is a VIEW, nothing is persisted for it.
            var watch = all.FirstOrDefault(i =>
                i.Type == AgentTaskTypes.PrWatch
                && string.Equals(i.GetMetadata("taskId"), issue.Id, StringComparison.Ordinal));
            var extractionRuns = extractions is null
                ? (IReadOnlyList<Orchestrator.MemoryExtractionRecord>)Array.Empty<Orchestrator.MemoryExtractionRecord>()
                : await extractions.ListForTaskAsync(issue.Id, ct);
            string? specId = hasSpecChain ? SprintAssembler.ResolveGroupKey(issue, byId) : null;
            var spec = specId is not null ? await specs.GetAsync(specId, ct) : null;

            var visits = new List<object>();
            for (var i = 0; i < journey.Count; i++)
            {
                var v = journey[i];
                var durationMs = i + 1 < journey.Count
                    ? (long)(journey[i + 1].At - v.At).TotalMilliseconds
                    : (long)(DateTime.UtcNow - v.At).TotalMilliseconds;
                visits.Add(new
                {
                    node = v.Node,
                    at = v.At,
                    note = v.Note,
                    count = v.Count,
                    durationMs,
                    details = StageDetails(v.Node, issue, watch, spec, active, sprintMembers, extractionRuns),
                });
            }

            return (IResult)Results.Json(new
            {
                issueId = issue.Id,
                title = issue.Title,
                status = issue.Status.ToString(),
                currentNode,
                // The state machine's record on this issue (when the
                // task has been through the machine).
                lifecycleState = issue.GetMetadata("state"),
                lifecycleEvent = issue.GetMetadata("lastEvent"),
                lifecycleStateEnteredAt = issue.GetMetadata("stateEnteredAt"),
                lifecycleViolation = issue.GetMetadata("stateViolation"),
                prNumber = issue.GetMetadata("prNumber"),
                visits,
            });
        });
    }

    private static object? StageDetails(
        string node,
        IssueRecord issue,
        IssueRecord? watch,
        SpecRecord? spec,
        SprintRecord? activeSprint,
        HashSet<string> sprintMembers,
        IReadOnlyList<Orchestrator.MemoryExtractionRecord> extractionRuns)
    {
        string? Meta(string key) => issue.GetMetadata(key);
        return node switch
        {
            "groom" => new
            {
                outcome = Meta("groomed") == "true" ? "approved" : Meta("groomCloseReason") is not null ? "closed" : "pending",
                note = Meta("groomNote"),
                closeReason = Meta("groomCloseReason"),
                runId = Meta("groomRunId"),
                groomedAt = Meta("groomedAt"),
            },
            "backlog" => spec is not null
                ? new { specId = spec.Id, specTitle = spec.Title, specStatus = spec.Status.ToString() }
                : null,
            "sprint" => activeSprint is not null && sprintMembers.Contains(issue.Id)
                ? new { sprintId = activeSprint.Id, name = activeSprint.Name, goal = activeSprint.Goal }
                : null,
            "setup" => new
            {
                branch = Meta("branch"),
                worktree = Meta("worktreePath"),
            },
            "agent" => new
            {
                noProgressAttempts = Meta("noProgressAttempts"),
                extractions = extractionRuns.Select(r => new
                {
                    at = r.Timestamp,
                    r.SourceChars,
                    r.ExtractedCount,
                    keys = r.PersistedKeys,
                    error = r.Error,
                }).ToArray(),
            },
            "pr" or "review" => new
            {
                prNumber = Meta("prNumber"),
                watchStatus = watch?.Status.ToString(),
                ciVerdict = watch?.GetMetadata("reviewVerdict"),
                reviewRound = watch?.GetMetadata("reviewRound"),
                reviewNotes = watch?.GetMetadata("reviewNotes"),
                reviewedSha = watch?.GetMetadata("reviewSha"),
            },
            "parked" => new
            {
                // Parked on pre-existing base-branch CI failure:
                // waiting for the base to recover; no strikes burn.
                parkedForSha = Meta("parkedForSha"),
                parkedAt = Meta("stateEnteredAt"),
                prNumber = Meta("prNumber"),
            },
            "rework" => new
            {
                attempts = Meta("reworkAttempts"),
                context = Meta("reworkContext"),
                prNumber = Meta("prNumber"),
                maxAttempts = 3,
            },
            "done" => new
            {
                closedAt = issue.ClosedAt,
                prNumber = Meta("prNumber"),
                merged = Meta("prNumber") is not null,
            },
            "blocked" => new
            {
                noProgressAttempts = Meta("noProgressAttempts"),
                reworkAttempts = Meta("reworkAttempts"),
                groomCloseReason = Meta("groomCloseReason"),
            },
            _ => null,
        };
    }
}
