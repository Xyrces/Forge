using Fluxor;

namespace Forge.Dashboard.Features.View;

public static class ViewReducers
{
    [ReducerMethod]
    public static ViewState OnLoad(ViewState state, ViewActions.LoadViewAction action)
        // Background reloads keep the current data on screen — the
        // Loading placeholder is for the FIRST load only, otherwise
        // every refresh blinks the whole page (operator 2026-07-31).
        => action.Background && state.LastFetchedAt is not null
            ? state with { Refreshing = true, Error = null }
            : state with { Loading = true, Error = null };

    [ReducerMethod]
    public static ViewState OnLoaded(ViewState state, ViewActions.ViewLoadedAction action)
        => state with
        {
            Loading = false,
            Refreshing = false,
            Error = null,
            LastFetchedAt = DateTime.UtcNow,
            Tasks = action.State.Tasks,
            Agents = action.State.Agents,
            Skills = action.State.Skills,
            Sprints = action.State.Sprints,
            CompletedTasks = action.State.CompletedTasks,
            FailedTasks = action.State.FailedTasks,
        };

    [ReducerMethod]
    public static ViewState OnLoadFailed(ViewState state, ViewActions.ViewLoadFailedAction action)
        => state with { Loading = false, Refreshing = false, Error = action.Error };
}
