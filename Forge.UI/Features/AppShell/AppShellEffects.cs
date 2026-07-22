using Fluxor;
using Microsoft.JSInterop;

namespace Forge.Dashboard.Features.AppShell;

public sealed class AppShellEffects
{
    /// <summary>localStorage key for the persisted project selection.</summary>
    public const string ProjectPrefsKey = "forge.currentProject";

    private readonly AppShellClient _client;
    private readonly IState<AppShellState> _state;
    private readonly IJSRuntime _js;

    public AppShellEffects(AppShellClient client, IState<AppShellState> state, IJSRuntime js)
    {
        _client = client;
        _state = state;
        _js = js;
    }

    [EffectMethod]
    public async Task HandleLoadActiveSprint(AppShellActions.LoadActiveSprintAction action, IDispatcher dispatcher)
    {
        var sprint = await _client.GetActiveSprintAsync(_state.Value.CurrentProjectId, CancellationToken.None);
        dispatcher.Dispatch(new AppShellActions.ActiveSprintLoadedAction(sprint?.Id, sprint?.Name));
    }

    [EffectMethod]
    public async Task HandlePollHeartbeat(AppShellActions.PollHeartbeatAction action, IDispatcher dispatcher)
    {
        var hb = await _client.GetHeartbeatAsync(CancellationToken.None);
        dispatcher.Dispatch(new AppShellActions.HeartbeatUpdatedAction(hb?.Status ?? "unreachable", hb?.At ?? DateTime.UtcNow));
    }

    [EffectMethod]
    public async Task HandleLoadProjects(AppShellActions.LoadProjectsAction action, IDispatcher dispatcher)
    {
        var projects = await _client.ListProjectsAsync(CancellationToken.None);

        // Resolve the selection: persisted (localStorage) if it still
        // names a registered project, otherwise keep the current state
        // selection, otherwise first project. JS interop throws during
        // prerender — treat as "no persisted value"; OnInitialized
        // runs again once the circuit is interactive and re-dispatches.
        var persisted = await TryGetPrefAsync();
        var current = _state.Value.CurrentProjectId;
        string? resolved =
            persisted is not null && projects.Any(p => p.Id == persisted) ? persisted :
            current is not null && projects.Any(p => p.Id == current) ? current :
            projects.FirstOrDefault()?.Id;

        dispatcher.Dispatch(new AppShellActions.ProjectsLoadedAction(projects, resolved));

        // Refresh the sprint pill through the (possibly new) lens.
        dispatcher.Dispatch(new AppShellActions.LoadActiveSprintAction());
    }

    [EffectMethod]
    public async Task HandleSelectProject(AppShellActions.SelectProjectAction action, IDispatcher dispatcher)
    {
        await TrySetPrefAsync(action.ProjectId);
        // Refresh the sprint pill for the new project. Pages subscribe
        // to AppShellState and reload their own data on the change.
        dispatcher.Dispatch(new AppShellActions.LoadActiveSprintAction());
    }

    private async Task<string?> TryGetPrefAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("forge.prefs.get", ProjectPrefsKey);
        }
        catch (InvalidOperationException)
        {
            return null; // prerender — JS not available yet
        }
        catch (JSException)
        {
            return null;
        }
    }

    private async Task TrySetPrefAsync(string projectId)
    {
        try
        {
            await _js.InvokeVoidAsync("forge.prefs.set", ProjectPrefsKey, projectId);
        }
        catch (InvalidOperationException)
        {
            // prerender — persistence is best-effort
        }
        catch (JSException)
        {
            // persistence is best-effort
        }
    }
}