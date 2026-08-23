using Fluxor;

namespace Forge.Dashboard.Features.Triage;

public static class TriageReducers
{
    [ReducerMethod]
    public static TriageState OnLoadLedger(TriageState state, TriageActions.LoadLedgerAction _)
        => state with { Loading = true, Error = null };

    [ReducerMethod]
    public static TriageState OnLedgerLoaded(TriageState state, TriageActions.LedgerLoadedAction action)
        => state with { Loading = false, Summary = action.Summary, Groups = action.Groups, Health = action.Health, Error = null };

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
}
