using Fluxor;

namespace Forge.Dashboard.Features.Art;

public sealed class ArtEffects
{
    private readonly ArtClient _client;
    public ArtEffects(ArtClient client) { _client = client; }

    [EffectMethod]
    public async Task HandleLoad(ArtActions.LoadArtAction action, IDispatcher dispatcher)
    {
        try
        {
            var rows = await _client.ListAsync(action.ProjectId, null, CancellationToken.None);
            dispatcher.Dispatch(new ArtActions.ArtLoadedAction(rows));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new ArtActions.ArtLoadFailedAction(ex.Message));
        }
    }
}