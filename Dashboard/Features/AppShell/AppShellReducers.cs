using Fluxor;

namespace Forge.Dashboard.Features.AppShell;

public static class AppShellReducers
{
    [ReducerMethod]
    public static AppShellState OnActiveSprintLoaded(AppShellState state, AppShellActions.ActiveSprintLoadedAction action)
        => state with { ActiveSprintId = action.Id, ActiveSprintName = action.Name };

    [ReducerMethod]
    public static AppShellState OnHeartbeatUpdated(AppShellState state, AppShellActions.HeartbeatUpdatedAction action)
        => state with { HeartbeatStatus = action.Status, LastHeartbeatAt = action.At };

    [ReducerMethod]
    public static AppShellState OnToggleLiveFeed(AppShellState state, AppShellActions.ToggleLiveFeedAction _)
        => state with { LiveFeedOpen = !state.LiveFeedOpen };

    [ReducerMethod]
    public static AppShellState OnNavigate(AppShellState state, AppShellActions.NavigateAction action)
        => state with { CurrentRoute = action.Route };

    [ReducerMethod]
    public static AppShellState OnPushLiveFeedEvent(AppShellState state, AppShellActions.PushLiveFeedEventAction action)
    {
        var next = state.LiveFeed.Concat(new[] { action.Entry }).TakeLast(200).ToArray();
        return state with { LiveFeed = next };
    }
}