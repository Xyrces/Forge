using Fluxor;

namespace Forge.Dashboard.Features.Designs;

public sealed class DesignsEffects
{
    private readonly DesignsClient _client;
    public DesignsEffects(DesignsClient client) { _client = client; }

    [EffectMethod]
    public async Task HandleLoad(DesignsActions.LoadDesignsAction action, IDispatcher dispatcher)
    {
        try
        {
            var rows = await _client.ListAsync(action.ProjectId, null, CancellationToken.None);
            dispatcher.Dispatch(new DesignsActions.DesignsLoadedAction(rows));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new DesignsActions.DesignsLoadFailedAction(ex.Message));
        }
    }
}