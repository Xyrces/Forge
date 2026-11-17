using System.Text.Json;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Completion;
using Microsoft.Extensions.Logging;

namespace Forge.Deploy;

public interface IRoutineReleaseSource
{
    Task<(string Sha, string Evidence)> ObserveMergedFrontierAsync(ProjectOptions project, IReadOnlyList<IssueRecord> members, CancellationToken ct);
    Task<TrustedImageReceipt?> FindReceiptAsync(ProjectOptions project, string sha, CancellationToken ct);
    Task ValidateForwardAsync(ProjectOptions project, string sha, CancellationToken ct);
}

public interface IIndependentDeploymentExecutor : IDeploymentExecutor
{
    Task<DeploymentExecutionResult> ReadProgressAsync(ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct, Func<CancellationToken, Task<bool>>? canStart = null);
}

public sealed record RoutineDeploymentProject(ProjectOptions Options, SprintCompletionStore Completions, DeploymentStore Deployments);

/// <summary>Durable completion is the trigger; dashboard reads are not a scheduler.</summary>
public sealed class RoutineDeploymentCoordinator(Func<IReadOnlyList<RoutineDeploymentProject>> projects,
    IRoutineReleaseSource source, Func<ProjectOptions, IIndependentDeploymentExecutor> executor,
    StageGates gates, ILogger<RoutineDeploymentCoordinator> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ReconcileAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Routine deployment reconciliation will retry from durable state"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        foreach (var project in projects())
        {
            if (project.Options.Deployment?.Kind != DeploymentKind.Kubernetes) continue;
            if (project.Options.Deployment.RoutineEnabled)
            {
                string? cursor = null;
                do
                {
                    var receipts = await project.Completions.ListAwaitingDeploymentAsync(afterId: cursor, ct: ct);
                    foreach (var receipt in receipts)
                    {
                        try
                        {
                            if (receipt.ProjectId != project.Options.Id || !receipt.ReadyForDeployment)
                                throw new InvalidOperationException("Completion is not authorized for this project's routine delivery.");
                            var sha = receipt.SourceSha;
                            if (sha is null)
                            {
                                var members = JsonSerializer.Deserialize<IssueRecord[]>(receipt.MembersJson, Json)
                                    ?? throw new InvalidOperationException("Completion member evidence is missing.");
                                var observed = await source.ObserveMergedFrontierAsync(project.Options, members, ct);
                                await project.Completions.RecordSourceAsync(receipt.Id, observed.Sha, observed.Evidence, ct);
                                sha = observed.Sha;
                            }
                            var candidate = await project.Deployments.CreateOrGetRoutineAsync(project.Options.Id, receipt.Id, sha, ct);
                            await project.Completions.BindDeploymentAsync(receipt.Id, candidate.Id, ct);
                            await project.Completions.RecordDeliveryWaitAsync(receipt.Id, null, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Completion {Completion} is awaiting verified delivery evidence", receipt.Id);
                            await project.Completions.RecordDeliveryWaitAsync(receipt.Id, "Awaiting verified merged source or deployment binding: " + ex.Message, ct);
                        }
                    }
                    cursor = receipts.Count == 50 ? receipts[^1].Id : null;
                } while (cursor is not null);
            }

            string? candidateCursor = null;
            do
            {
                var candidates = await project.Deployments.ListPendingAsync(project.Options.Id, candidateCursor, ct: ct);
                foreach (var item in candidates)
                {
                    try { await ReconcileCandidateAsync(project, item, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Deployment {Deployment} remains pending reconciliation", item.Id);
                        await project.Deployments.RecordQualificationWaitAsync(item.Id, "Awaiting qualified release: " + ex.Message, ct);
                    }
                }
                candidateCursor = candidates.Count == 50 ? candidates[^1].Id : null;
            } while (candidateCursor is not null);

            // Recover a stop after the independent release result committed but before
            // its completion projection advanced. Terminal deployments are absent from
            // the pending query, so this is a separate durable bounded scan.
            string? completionCursor = null;
            do
            {
                var receipts = await project.Completions.ListAwaitingDeliveryOutcomeAsync(afterId: completionCursor, ct: ct);
                foreach (var receipt in receipts)
                {
                    try { await project.Completions.RefreshDeploymentOutcomeAsync(receipt.Id, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Completion {Completion} is awaiting final delivery evidence", receipt.Id);
                        await project.Completions.RecordDeliveryWaitAsync(receipt.Id, "Final delivery evidence could not be confirmed: " + ex.Message, ct);
                    }
                }
                completionCursor = receipts.Count == 50 ? receipts[^1].Id : null;
            } while (completionCursor is not null);
        }
    }

    private async Task ReconcileCandidateAsync(RoutineDeploymentProject project, DeploymentCandidate candidate, CancellationToken ct)
    {
        if (candidate.CompletionId is not null && candidate.Status is DeploymentStatus.Pending or DeploymentStatus.BuildPassed)
        {
            if (!project.Options.Deployment!.RoutineEnabled) return;
            var completion = await project.Completions.GetAsync(candidate.CompletionId, ct);
            if (completion is null || !completion.ReadyForDeployment || completion.DeploymentCandidateId != candidate.Id ||
                completion.SourceSha != candidate.CommitSha || completion.ProjectId != project.Options.Id)
                throw new InvalidOperationException("Routine candidate no longer matches its authorized completion.");
            if (candidate.Status == DeploymentStatus.Pending)
            {
                var receipt = await source.FindReceiptAsync(project.Options, candidate.CommitSha, ct);
                if (receipt is null)
                {
                    await project.Deployments.RecordQualificationWaitAsync(candidate.Id, "Awaiting successful exact-source CI and immutable image receipt.", ct);
                    await project.Completions.RecordDeliveryWaitAsync(completion.Id, "Awaiting successful exact-source CI and immutable image receipt.", ct);
                    return;
                }
                await project.Deployments.PinReceiptAsync(candidate.Id, JsonSerializer.Serialize(receipt, Json), ct);
                candidate = (await project.Deployments.GetAsync(candidate.Id, ct))!;
            }
            if (await gates.IsHeldAsync(StageGates.Deploy, ct))
            {
                await project.Completions.RecordDeliveryWaitAsync(completion.Id, "Routine deployment is held by the operator deploy gate.", ct);
                return;
            }
            await source.ValidateForwardAsync(project.Options, candidate.CommitSha, ct);
            await project.Completions.RecordDeliveryWaitAsync(completion.Id, null, ct);
            if (!await project.Deployments.TryApproveAsync(candidate.Id, "approved initiative routine deployment policy", ct)) return;
            // The following durable Approved row is sufficient to recover a
            // process stop before the independent Job is created.
            candidate = candidate with { Status = DeploymentStatus.Approved };
        }
        if (candidate.Status is not (DeploymentStatus.Approved or DeploymentStatus.Deploying)) return;
        var release = executor(project.Options);
        var progress = await release.ReadProgressAsync(project.Options, candidate, ct, async token => !await gates.IsHeldAsync(StageGates.Deploy, token));
        await project.Deployments.MarkIndependentProgressAsync(candidate.Id, progress, ct);
    }
}
