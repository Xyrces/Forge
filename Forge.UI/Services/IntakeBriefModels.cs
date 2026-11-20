namespace Forge.Dashboard.Services;

public sealed record IntakeProposal(string Id, string ProjectId, int Revision, string Hash, string Title,
    string Origin, string? SessionId, DateTime? ApprovedAt, string? ApprovedBy,
    string? EpicId, string? SpecId, string? Body = null, string? SourceJson = null, int Priority = 2);
public sealed record IntakeProposalPage(List<IntakeProposal> Items, string? NextCursor);
public sealed record IntakeIngestion(string EpicId, string SpecId, string RefinementState, string? LastError);
public sealed record IntakeProposalDetail(IntakeProposal Proposal, IntakeIngestion? Ingestion);
public sealed record IntakeProposalRevision(int Revision, string Title, string Body, int Priority, string Hash);
public sealed record IntakeBriefSpec(string Id, string Status);
