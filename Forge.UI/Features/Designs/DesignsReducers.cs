using Fluxor;

namespace Forge.Dashboard.Features.Designs;

public static class DesignsReducers
{
    [ReducerMethod]
    public static DesignsState OnLoadDesigns(DesignsState state, DesignsActions.LoadDesignsAction _)
        => state with { Loading = true, Error = null };

    [ReducerMethod]
    public static DesignsState OnLoaded(DesignsState state, DesignsActions.DesignsLoadedAction action)
        => state with { Loading = false, Rows = action.Rows, Error = null };

    [ReducerMethod]
    public static DesignsState OnFailed(DesignsState state, DesignsActions.DesignsLoadFailedAction action)
        => state with { Loading = false, Error = action.Error };
}