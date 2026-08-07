using Microsoft.JSInterop;

namespace Forge.Dashboard.Services;

/// <summary>
/// Scoped server-push channel: wraps the browser EventSource over
/// /api/events (see wwwroot/events.js) and re-raises named server
/// events to Blazor components. Pages subscribe and trigger
/// BACKGROUND store reloads — no polling, no full-panel refresh
/// (operator direction 2026-07-31: the board should be
/// event-driven).
/// </summary>
public sealed class DashboardEventStream : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<DashboardEventStream>? _ref;
    private bool _started;

    public DashboardEventStream(IJSRuntime js) => _js = js;

    /// <summary>Fired on the sync context for every subscribed
    /// server event kind (dash-sanitized, e.g. "task-transition").</summary>
    public event Action<string>? Received;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        _ref = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("forge.events.subscribe", _ref);
    }

    [JSInvokable]
    public void OnServerEvent(string kind) => Received?.Invoke(kind);

    public async ValueTask DisposeAsync()
    {
        if (!_started) return;
        _started = false;
        try
        {
            await _js.InvokeVoidAsync("forge.events.unsubscribe");
        }
        catch (JSDisconnectedException) { /* circuit already gone */ }
        catch (InvalidOperationException) { /* prerender: no JS runtime yet */ }
        _ref?.Dispose();
    }
}
