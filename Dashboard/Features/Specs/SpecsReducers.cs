using Fluxor;

namespace Forge.Dashboard.Features.Specs;

public static class SpecsReducers
{
    [ReducerMethod]
    public static SpecsState OnLoadSpecs(SpecsState state, SpecsActions.LoadSpecsAction _)
        => state with { Loading = true, Error = null };

    [ReducerMethod]
    public static SpecsState OnSpecsLoaded(SpecsState state, SpecsActions.SpecsLoadedAction action)
        => state with { Loading = false, Rows = action.Rows, Error = null };

    [ReducerMethod]
    public static SpecsState OnSpecsLoadFailed(SpecsState state, SpecsActions.SpecsLoadFailedAction action)
        => state with { Loading = false, Error = action.Error };

    [ReducerMethod]
    public static SpecsState OnSetStatusFilter(SpecsState state, SpecsActions.SetStatusFilterAction action)
        => state with { StatusFilter = action.Filter };
}