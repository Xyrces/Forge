using Fluxor;

namespace Forge.Dashboard.Features.AppShell;

public sealed class AppShellEffects
{
    private readonly AppShellClient _client;

    public AppShellEffects(AppShellClient client)
    {
        _client = client;
    }

    [EffectMethod]
    public async Task HandleLoadActiveSprint(AppShellActions.LoadActiveSprintAction action, IDispatcher dispatcher)
    {
        var sprint = await _client.GetActiveSprintAsync(CancellationToken.None);
        dispatcher.Dispatch(new AppShellActions.ActiveSprintLoadedAction(sprint?.Id, sprint?.Name));
    }

    [EffectMethod]
    public async Task HandlePollHeartbeat(AppShellActions.PollHeartbeatAction action, IDispatcher dispatcher)
    {
        var hb = await _client.GetHeartbeatAsync(CancellationToken.None);
        dispatcher.Dispatch(new AppShellActions.HeartbeatUpdatedAction(hb?.Status ?? "unreachable", hb?.At ?? DateTime.UtcNow));
    }
}