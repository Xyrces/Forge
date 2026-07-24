using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Forge.Core;
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
        ISprintStore sprints)
    {
        app.MapGet("/api/flow", async (CancellationToken ct) =>
        {
            var counts = FlowGraph.Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
            var samples = FlowGraph.Nodes.ToDictionary(
                n => n.Id, _ => new List<object>(MaxSamplesPerNode), StringComparer.Ordinal);
            void Add(string nodeId, string id, string title, string status)
            {
                counts[nodeId]++;
                if (samples[nodeId].Count < MaxSamplesPerNode)
                {
                    samples[nodeId].Add(new { id, title, status });
                }
            }

            // Planning lane: specs still in planning.
            foreach (var spec in await specs.ListAsync(projectId: null, status: null, ct))
            {
                var node = FlowGraph.ClassifySpec(spec.Status);
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
                lanes = new[] { FlowGraph.LanePlanning, FlowGraph.LaneImplementation },
                nodes = FlowGraph.Nodes.Select(n => new
                {
                    id = n.Id,
                    label = n.Label,
                    lane = n.Lane,
                    x = n.X,
                    y = n.Y,
                    count = counts[n.Id],
                    issues = samples[n.Id],
                }),
                edges = FlowGraph.Edges.Select(e => new { from = e.From, to = e.To }),
                activeSprintId = active?.Id,
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
                events.Select(e => (e.Kind, e.Timestamp, (string?)e.Detail)).ToList(),
                currentNode);

            return (IResult)Results.Json(new
            {
                issueId = issue.Id,
                title = issue.Title,
                status = issue.Status.ToString(),
                currentNode,
                prNumber = issue.GetMetadata("prNumber"),
                visits = journey.Select(v => new { node = v.Node, at = v.At, note = v.Note, count = v.Count }),
            });
        });
    }
}
