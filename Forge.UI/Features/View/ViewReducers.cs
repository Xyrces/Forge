using Fluxor;

namespace Forge.Dashboard.Features.View;

public static class ViewReducers
{
    [ReducerMethod]
    public static ViewState OnLoad(ViewState state, ViewActions.LoadViewAction _)
        => state with { Loading = true, Error = null };

    [ReducerMethod]
    public static ViewState OnLoaded(ViewState state, ViewActions.ViewLoadedAction action)
        => state with
        {
            Loading = false,
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
        => state with { Loading = false, Error = action.Error };
}
