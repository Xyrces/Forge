namespace Forge.Core;

/// <summary>
/// Action-availability rules for a spec — the SINGLE source of
/// truth consumed by both /api/specs/{id}/actions and the
/// dashboard's action buttons (list + detail). The 2026-07-27 UI
/// audit found the Specs list shipping its own (wrong) copy:
/// Approve offered on ReadyForDesign/NeedsRevision, which the
/// server state machine rejects.
/// </summary>
public static class SpecActions
{
    public static bool CanApprove(SpecStatus status) => status == SpecStatus.Draft;
    public static bool CanStartGrooming(SpecStatus status) =>
        status is SpecStatus.Approved or SpecStatus.AssetReady;
    // Retained for API compatibility; delivery is established by completion evidence, not a manual label.
    public static bool CanShip(SpecStatus status) => false;
    public static bool CanSendToDesign(SpecStatus status) => status == SpecStatus.Draft;
}
