using Forge.Core;
using Forge.Core.Initiatives;
using Forge.Dashboard;

namespace Forge.Agents;

/// <summary>Operator approval and resumable cross-store ingestion. Chat is never the approval ledger.</summary>
public sealed class InitiativeApprovalService(
    InitiativeProposalStore proposals,
    Func<string, InitiativeIngestionStore> ingestionForProject,
    IDashboardEventBus events)
{
    public InitiativeProposalStore Proposals { get; } = proposals;

    public Task<InitiativeIngestion?> ReceiptAsync(string projectId, string id, int revision, CancellationToken ct = default)
        => ingestionForProject(projectId).GetAsync(id, revision, ct);

    public async Task<bool> RetryRefinementAsync(string projectId, string id, int revision, CancellationToken ct = default)
    {
        var proposal = await Proposals.GetAsync(projectId, id, ct);
        if (proposal?.ApprovedAt is null || proposal.Revision != revision)
            return false;
        var retry = await ingestionForProject(projectId).RetryRefinementAsync(id, revision, ct);
        if (retry)
            events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.refinement.retry", proposal.SessionId, "Operator requested refinement retry"));
        return retry;
    }

    public async Task<InitiativeIngestion> ApproveAsync(string projectId, string proposalId,
        int revision, string hash, CancellationToken ct = default)
    {
        // This private dashboard has no authenticated user identity. Do not
        // manufacture one or accept model-provided attribution.
        var approved = await Proposals.ApproveAsync(projectId, proposalId, revision, hash,
            "operator via private dashboard", ct);
        return await IngestApprovedAsync(approved, ct);
    }

    public async Task<InitiativeIngestion> IngestApprovedAsync(InitiativeProposal approved, CancellationToken ct = default)
    {
        var receipt = await ingestionForProject(approved.ProjectId).IngestAsync(approved, ct);
        // A failure here is recoverable: the target receipt already commits
        // the unique epic/spec pair; replay adopts it before acknowledging.
        await Proposals.MarkIngestedAsync(approved.ProjectId, approved.Id, approved.Revision,
            receipt.EpicId, receipt.SpecId, ct);
        if (approved.EpicId is null)
            events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.epic.accepted", approved.SessionId,
            "Approved initiative ingested", new Dictionary<string, object?>
            {
                ["projectId"] = approved.ProjectId,
                ["epicId"] = receipt.EpicId,
                ["proposalId"] = approved.Id,
                ["revision"] = approved.Revision
            }));
        return receipt;
    }
}
