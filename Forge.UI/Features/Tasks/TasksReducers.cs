using Fluxor;

namespace Forge.Dashboard.Features.Tasks;

public static class TasksReducers
{
    [ReducerMethod]
    public static TasksState OnLoadTasks(TasksState state, TasksActions.LoadTasksAction _)
        => state with { Loading = true, Error = null };

    [ReducerMethod]
    public static TasksState OnTasksLoaded(TasksState state, TasksActions.TasksLoadedAction action)
        => state with { Loading = false, Rows = action.Rows, Error = null };

    [ReducerMethod]
    public static TasksState OnTasksLoadFailed(TasksState state, TasksActions.TasksLoadFailedAction action)
        => state with { Loading = false, Error = action.Error };

    [ReducerMethod]
    public static TasksState OnRetryMessageSucceeded(TasksState state, TasksActions.RetryMessageSucceededAction action)
        => state with { ActionMessage = $"Message queued for {action.TaskId}", ActionError = null };

    [ReducerMethod]
    public static TasksState OnRetryMessageFailed(TasksState state, TasksActions.RetryMessageFailedAction action)
        => state with { ActionError = action.Error, ActionMessage = null };

    [ReducerMethod]
    public static TasksState OnRecoverSucceeded(TasksState state, TasksActions.RecoverTaskSucceededAction action)
        => state with { ActionMessage = $"Recovery started for {action.TaskId} (report #{action.ReportId})", ActionError = null };

    [ReducerMethod]
    public static TasksState OnRecoverFailed(TasksState state, TasksActions.RecoverTaskFailedAction action)
        => state with { ActionError = action.Error, ActionMessage = null };
}
