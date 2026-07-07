using Fluxor;

namespace Forge.Dashboard.Features.Tasks;

public sealed class TasksEffects
{
    private readonly TasksClient _client;

    public TasksEffects(TasksClient client)
    {
        _client = client;
    }

    [EffectMethod]
    public async Task HandleLoadTasks(TasksActions.LoadTasksAction action, IDispatcher dispatcher)
    {
        try
        {
            var rows = await _client.ListInProgressAsync(CancellationToken.None);
            dispatcher.Dispatch(new TasksActions.TasksLoadedAction(rows));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new TasksActions.TasksLoadFailedAction(ex.Message));
        }
    }

    [EffectMethod]
    public async Task HandleRetryMessage(TasksActions.RetryMessageAction action, IDispatcher dispatcher)
    {
        try
        {
            await _client.RetryMessageAsync(action.TaskId, action.Text, CancellationToken.None);
            dispatcher.Dispatch(new TasksActions.RetryMessageSucceededAction(action.TaskId));
            dispatcher.Dispatch(new TasksActions.LoadTasksAction());
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new TasksActions.RetryMessageFailedAction(ex.Message));
        }
    }

    [EffectMethod]
    public async Task HandleRecoverTask(TasksActions.RecoverTaskAction action, IDispatcher dispatcher)
    {
        try
        {
            var result = await _client.RecoverAsync(action.TaskId, CancellationToken.None);
            dispatcher.Dispatch(new TasksActions.RecoverTaskSucceededAction(action.TaskId, result?.ReportId));
            dispatcher.Dispatch(new TasksActions.LoadTasksAction());
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new TasksActions.RecoverTaskFailedAction(ex.Message));
        }
    }
}
