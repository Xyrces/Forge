using Fluxor;

namespace Forge.Dashboard.Features.Triage;

public sealed class TriageEffects
{
    private readonly TriageClient _client;

    public TriageEffects(TriageClient client)
    {
        _client = client;
    }

    [EffectMethod]
    public async Task HandleLoadLedger(TriageActions.LoadLedgerAction action, IDispatcher dispatcher)
    {
        try
        {
            var (summary, groups, health, enabled) = await _client.GetLedgerAsync(action.ProjectId, CancellationToken.None);
            dispatcher.Dispatch(new TriageActions.LedgerLoadedAction(summary, groups, health, enabled));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new TriageActions.LedgerLoadFailedAction(ex.Message));
        }
    }

    [EffectMethod]
    public async Task HandleExpandSignature(TriageActions.ExpandSignatureAction action, IDispatcher dispatcher)
    {
        try
        {
            var rows = await _client.GetSignatureRowsAsync(action.Signature, action.ProjectId, CancellationToken.None);
            dispatcher.Dispatch(new TriageActions.SignatureDetailLoadedAction(action.Signature, rows));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new TriageActions.SignatureDetailFailedAction(ex.Message));
        }
    }

    [EffectMethod]
    public async Task HandleToggleTriage(TriageActions.ToggleTriageAction action, IDispatcher dispatcher)
    {
        if (action.ProjectId is null)
        {
            dispatcher.Dispatch(new TriageActions.TriageToggleFailedAction("no project selected"));
            return;
        }
        try
        {
            var ok = await _client.SetTriageEnabledAsync(action.ProjectId, action.Enabled, CancellationToken.None);
            if (ok)
                dispatcher.Dispatch(new TriageActions.TriageToggledAction(action.Enabled));
            else
                dispatcher.Dispatch(new TriageActions.TriageToggleFailedAction("PUT triage flag failed"));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new TriageActions.TriageToggleFailedAction(ex.Message));
        }
    }
}
