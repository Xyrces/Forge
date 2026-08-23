using Fluxor;

namespace Forge.Dashboard.Features.Triage;

public static class TriageReducers
{
    [ReducerMethod]
    public static TriageState OnLoadLedger(TriageState state, TriageActions.LoadLedgerAction _)
        => state with { Loading = true, Error = null };

    [ReducerMethod]
    public static TriageState OnLedgerLoaded(TriageState state, TriageActions.LedgerLoadedAction action)
        => state with { Loading = false, Summary = action.Summary, Groups = action.Groups, Health = action.Health, TriageEnabled = action.TriageEnabled, Error = null };

    [ReducerMethod]
    public static TriageState OnLedgerLoadFailed(TriageState state, TriageActions.LedgerLoadFailedAction action)
        => state with { Loading = false, Error = action.Error };

    [ReducerMethod]
    public static TriageState OnExpandSignature(TriageState state, TriageActions.ExpandSignatureAction action)
        => state with { ExpandedSignature = action.Signature, DetailLoading = true, DetailRows = Array.Empty<TriageEntryRow>() };

    [ReducerMethod]
    public static TriageState OnCollapseSignature(TriageState state, TriageActions.CollapseSignatureAction _)
        => state with { ExpandedSignature = null, DetailLoading = false, DetailRows = Array.Empty<TriageEntryRow>() };

    [ReducerMethod]
    public static TriageState OnSignatureDetailLoaded(TriageState state, TriageActions.SignatureDetailLoadedAction action)
        => state.ExpandedSignature == action.Signature
            ? state with { DetailLoading = false, DetailRows = action.Rows }
            : state;

    [ReducerMethod]
    public static TriageState OnSignatureDetailFailed(TriageState state, TriageActions.SignatureDetailFailedAction action)
        => state with { DetailLoading = false, Error = action.Error };

    [ReducerMethod]
    public static TriageState OnToggleTriage(TriageState state, TriageActions.ToggleTriageAction _)
        => state with { ToggleInFlight = true, Error = null };

    [ReducerMethod]
    public static TriageState OnTriageToggled(TriageState state, TriageActions.TriageToggledAction action)
        => state with { ToggleInFlight = false, TriageEnabled = action.Enabled };

    [ReducerMethod]
    public static TriageState OnTriageToggleFailed(TriageState state, TriageActions.TriageToggleFailedAction action)
        => state with { ToggleInFlight = false, Error = action.Error };
}
