using Fluxor;

namespace Forge.Dashboard.Features.Deployments;

public sealed class LoadDeploymentsEffect : Effect<DeploymentsActions.LoadDeploymentsAction>
{
    private readonly DeploymentsClient _client;
    public LoadDeploymentsEffect(DeploymentsClient client) { _client = client; }

    public override async Task HandleAsync(DeploymentsActions.LoadDeploymentsAction action, IDispatcher dispatcher)
    {
        try
        {
            var rows = await _client.ListAsync(action.ProjectId, CancellationToken.None);
            dispatcher.Dispatch(new DeploymentsActions.DeploymentsLoadedAction(rows));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new DeploymentsActions.DeploymentsLoadFailedAction(ex.Message));
        }
    }
}

public sealed class LoadCommitsEffect : Effect<DeploymentsActions.LoadCommitsAction>
{
    private readonly DeploymentsClient _client;
    public LoadCommitsEffect(DeploymentsClient client) { _client = client; }

    public override async Task HandleAsync(DeploymentsActions.LoadCommitsAction action, IDispatcher dispatcher)
    {
        try
        {
            var rows = await _client.ListCommitsAsync(action.ProjectId, CancellationToken.None);
            dispatcher.Dispatch(new DeploymentsActions.CommitsLoadedAction(action.ProjectId, rows));
        }
        catch
        {
            dispatcher.Dispatch(new DeploymentsActions.CommitsLoadedAction(action.ProjectId, Array.Empty<CommitRow>()));
        }
    }
}

public sealed class RequestDeploymentEffect : Effect<DeploymentsActions.RequestDeploymentAction>
{
    private readonly DeploymentsClient _client;
    public RequestDeploymentEffect(DeploymentsClient client) { _client = client; }

    public override async Task HandleAsync(DeploymentsActions.RequestDeploymentAction action, IDispatcher dispatcher)
    {
        try
        {
            await _client.RequestAsync(action.ProjectId, action.CommitSha, action.RequestedBy, CancellationToken.None);
            dispatcher.Dispatch(new DeploymentsActions.LoadDeploymentsAction());
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new DeploymentsActions.RequestDeploymentFailedAction(ex.Message));
        }
    }
}

public sealed class ApproveDeploymentEffect : Effect<DeploymentsActions.ApproveDeploymentAction>
{
    private readonly DeploymentsClient _client;
    public ApproveDeploymentEffect(DeploymentsClient client) { _client = client; }

    public override async Task HandleAsync(DeploymentsActions.ApproveDeploymentAction action, IDispatcher dispatcher)
    {
        try
        {
            var (success, blockedMessage) = await _client.ApproveAsync(action.Id, action.Force, CancellationToken.None);
            if (!success && blockedMessage is not null)
            {
                dispatcher.Dispatch(new DeploymentsActions.ApproveDeploymentBlockedAction(action.Id, blockedMessage));
                return;
            }
            dispatcher.Dispatch(new DeploymentsActions.LoadDeploymentsAction());
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new DeploymentsActions.ApproveDeploymentFailedAction(ex.Message));
        }
    }
}

public sealed class RejectDeploymentEffect : Effect<DeploymentsActions.RejectDeploymentAction>
{
    private readonly DeploymentsClient _client;
    public RejectDeploymentEffect(DeploymentsClient client) { _client = client; }

    public override async Task HandleAsync(DeploymentsActions.RejectDeploymentAction action, IDispatcher dispatcher)
    {
        try
        {
            await _client.RejectAsync(action.Id, CancellationToken.None);
            dispatcher.Dispatch(new DeploymentsActions.LoadDeploymentsAction());
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new DeploymentsActions.ApproveDeploymentFailedAction(ex.Message));
        }
    }
}
