using Fluxor;

namespace Forge.Dashboard.Features.Art;

public static class ArtReducers
{
    [ReducerMethod]
    public static ArtState OnLoad(ArtState state, ArtActions.LoadArtAction _)
        => state with { Loading = true, Error = null };

    [ReducerMethod]
    public static ArtState OnLoaded(ArtState state, ArtActions.ArtLoadedAction action)
        => state with { Loading = false, Rows = action.Rows, Error = null };

    [ReducerMethod]
    public static ArtState OnFailed(ArtState state, ArtActions.ArtLoadFailedAction action)
        => state with { Loading = false, Error = action.Error };
}