using Fluxor;

namespace Forge.Dashboard.Features.View;

public sealed class ViewEffects
{
    private readonly ViewClient _client;

    public ViewEffects(ViewClient client)
    {
        _client = client;
    }

    [EffectMethod]
    public async Task HandleLoad(ViewActions.LoadViewAction action, IDispatcher dispatcher)
    {
        try
        {
            var snapshot = await _client.FetchAsync(CancellationToken.None);
            dispatcher.Dispatch(new ViewActions.ViewLoadedAction(new ViewState
            {
                Tasks = snapshot.Tasks,
                Agents = snapshot.Agents,
                Skills = snapshot.Skills,
                Sprints = snapshot.Sprints,
                CompletedTasks = snapshot.CompletedTasks,
                FailedTasks = snapshot.FailedTasks,
            }));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new ViewActions.ViewLoadFailedAction(ex.Message));
        }
    }
}
