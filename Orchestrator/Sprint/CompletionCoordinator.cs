using System.Text.Json;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Core.Db;
using Forge.Dashboard;
using Forge.Core.Initiatives;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator.Sprint;

/// <summary>Resumes durable completion work. The active sprint is not its retry queue.</summary>
internal sealed class CompletionCoordinator(IssueStore issues, IFollowUpTriage? triage,
    InitiativeProposalStore? proposals, ILogger logger, IDashboardEventBus? events = null)
{
    private readonly SprintCompletionStore store = new(issues);
    private bool recovered;
    private readonly Dictionary<string, string> failureAcknowledgements = new();

    public async Task<SprintCompletionRecord> CompleteAsync(SprintRecord sprint, ISprintStore sprints, CancellationToken ct, bool routineDeploymentEnabled = false)
    {
        var members = new List<IssueRecord>();
        foreach (var id in await sprints.GetIssueIdsAsync(sprint.Id, ct))
        {
            var member = await issues.GetAsync(id, ct) ?? throw new InvalidOperationException("Sprint member missing; completion requires investigation.");
            if (!AgentTaskTypes.IsContainer(member.Type) && member.Type != AgentTaskTypes.PrWatch) members.Add(member);
        }
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,weights_json FROM {issues.Db.Dialect.Table("sprint_proposal_audit")} WHERE committed_sprint_id=@sprint ORDER BY id DESC";
        command.AddParam("@sprint", sprint.Id);
        long? auditId = null; SprintPlan? plan = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) { auditId = Convert.ToInt64(reader.GetValue(0)); plan = SprintPlanJson.ReadAuditPlan(reader.GetString(1)); }
        if (auditId is not null && plan is null) throw new InvalidOperationException("Committed sprint plan is invalid; completion cannot invent acceptance.");
        var context = members.ToDictionary(i => i.Id, StringComparer.Ordinal);
        foreach (var reference in members.Select(m => m.GetMetadata("closeReferenceIssueId")).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (!context.ContainsKey(reference) && await issues.GetAsync(reference, ct) is { } resolved)
                context.Add(reference, resolved);
        }
        var incomplete = members.Any(m => !IssueResolution.IsSatisfied(m, context));
        var verified = members.Count > 0 && members.All(TaskDeliveryEvidence.IsMergedAndVerified);
        var outcome = incomplete ? EngineeringOutcome.Incomplete
            : plan is null ? EngineeringOutcome.LegacyTerminalOnly
            : !verified || routineDeploymentEnabled || plan.Criteria.Any(c => c.EvidenceKind is SprintEvidenceKind.DeploymentHealthy or SprintEvidenceKind.OperatorObserved)
                ? EngineeringOutcome.PendingEvidence : EngineeringOutcome.Satisfied;
        return await store.BeginCompletionAsync(sprint.Id, members, new(auditId, plan?.Hash, outcome,
            JsonSerializer.Serialize(new { verifiedTasks = members.Where(TaskDeliveryEvidence.IsMergedAndVerified).Select(m => m.Id),
                unresolvedTasks = members.Where(m => !IssueResolution.IsSatisfied(m, context)).Select(m => m.Id),
                deploymentPending = plan?.Criteria.Any(c => c.EvidenceKind is SprintEvidenceKind.DeploymentHealthy or SprintEvidenceKind.OperatorObserved) ?? false }),
            ReadyForDeployment: plan is not null && verified && !incomplete,
            DeliveryRequired: routineDeploymentEnabled || (plan?.Criteria.Any(c => c.EvidenceKind is SprintEvidenceKind.DeploymentHealthy or SprintEvidenceKind.OperatorObserved) ?? false)), ct);
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (!recovered) { await store.RecoverRunningAsync(ct); recovered = true; }
        foreach (var (id, error) in failureAcknowledgements.ToArray())
        {
            await store.RecordTriageFailureAsync(id, error, ct);
            failureAcknowledgements.Remove(id);
        }
        string? cursor = null;
        while (true)
        {
            var receipts = await store.ListPendingBookkeepingAsync(afterId: cursor, ct: ct);
            foreach (var receipt in receipts)
            {
                var drafts = await store.ListUnclaimedDraftsAsync(receipt.Id, ct);
                if (drafts.Count > 0) await store.ClaimBatchAsync(receipt.Id, drafts, ct);
                foreach (var batch in await store.ListBatchesAsync(receipt.Id, ct, actionableOnly: true))
                {
                    if (batch.State == "Completed" || batch.State == "NeedsReview") continue;
                    try
                    {
                    var sources = JsonSerializer.Deserialize<FollowUpDraft[]>(batch.SourcesJson)!;
                    FollowUpTriageDecision? decision = batch.DecisionJson is null ? null : JsonSerializer.Deserialize<FollowUpTriageDecision>(batch.DecisionJson);
                    if (decision is null)
                    {
                        if (!await store.TryBeginTriageAsync(batch.Id, ct)) continue;
                        try
                        {
                            decision = triage is not null && sources.Length >= 2
                                ? await triage.TriageAsync(issues.ProjectId, sources, ct) : null;
                            decision ??= new(sources.Select(s => new TriageItem("create", [s.Id])).ToArray());
                            var known = sources.Select(s => s.Id).ToHashSet();
                            decision = new(decision.Items.Select(i => i with { SourceDraftIds = i.SourceDraftIds.Where(known.Contains).ToArray() })
                                .Where(i => i.SourceDraftIds.Count > 0).ToArray());
                            // Unmentioned sources retain the existing deterministic 1:1 fallback.
                            var cited = decision.Items.SelectMany(i => i.SourceDraftIds).ToHashSet();
                            decision = new(decision.Items.Concat(sources.Where(s => !cited.Contains(s.Id))
                                .Select(s => new TriageItem("create", [s.Id]))).ToArray());
                            await store.SaveDecisionAsync(batch.Id, decision, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Completion triage {Batch} requires review; generation will not repeat automatically", batch.Id);
                            var error = "Triage failed or its result could not be persisted: " + ex.GetType().Name;
                            failureAcknowledgements[batch.Id] = error;
                            await store.RecordTriageFailureAsync(batch.Id, error, ct);
                            failureAcknowledgements.Remove(batch.Id);
                            continue;
                        }
                    }
                    var created = 0;
                    for (var index = 0; index < decision.Items.Count; index++)
                    {
                        var item = decision.Items[index]; string? proposalId = null;
                        if (item.Action == "epic")
                        {
                            if (proposals is null) throw new InvalidOperationException("Initiative proposal store unavailable.");
                            var source = sources.Where(s => item.SourceDraftIds.Contains(s.Id)).ToArray();
                            var proposal = await proposals.CreateAsync(new(issues.ProjectId, batch.Id + ":" + index, "forge",
                                item.Title ?? source[0].Title, item.Description ?? string.Join("\n\n", source.Select(s => s.Description)),
                                item.Priority ?? source.Min(s => s.Priority), JsonSerializer.Serialize(new { sourceDraftIds = item.SourceDraftIds, completionId = receipt.Id })), ct);
                            proposalId = proposal.Id;
                        }
                        var materialized = await store.MaterializeItemWithResultAsync(batch.Id, index, proposalId, ct);
                        if (materialized.Created) created++;
                    }
                    events?.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.SprintMaterialized, null,
                        "Completion follow-up batch reconciled", new Dictionary<string, object?>
                        { ["projectId"] = issues.ProjectId, ["created"] = created, ["batchId"] = batch.Id }));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Completion batch {Batch} could not materialize; other batches remain runnable", batch.Id);
                        await store.RecordMaterializationFailureAsync(batch.Id, "Materialization retry pending: " + ex.GetType().Name, ct);
                    }
                }
                await store.MarkBookkeepingCompleteAsync(receipt.Id, ct);
            }
            if (receipts.Count < 50) break;
            cursor = receipts[^1].Id;
        }
    }
}
