using Fluxor;

namespace Forge.Dashboard.Features.Specs;

public sealed class SpecsEffects
{
    private readonly SpecsClient _client;

    public SpecsEffects(SpecsClient client)
    {
        _client = client;
    }

    [EffectMethod]
    public async Task HandleLoadSpecs(SpecsActions.LoadSpecsAction action, IDispatcher dispatcher)
    {
        try
        {
            var rows = await _client.ListAsync(null, action.ProjectId, CancellationToken.None);
            dispatcher.Dispatch(new SpecsActions.SpecsLoadedAction(rows));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new SpecsActions.SpecsLoadFailedAction(ex.Message));
        }
    }

    [EffectMethod]
    public async Task HandleFilter(SpecsActions.SetStatusFilterAction action, IDispatcher dispatcher)
    {
        try
        {
            var rows = await _client.ListAsync(action.Filter, action.ProjectId, CancellationToken.None);
            dispatcher.Dispatch(new SpecsActions.SpecsLoadedAction(rows));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new SpecsActions.SpecsLoadFailedAction(ex.Message));
        }
    }
}