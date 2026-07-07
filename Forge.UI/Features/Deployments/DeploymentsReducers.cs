using Fluxor;

namespace Forge.Dashboard.Features.Deployments;

public static class DeploymentsReducers
{
    [ReducerMethod(typeof(DeploymentsActions.LoadDeploymentsAction))]
    public static DeploymentsState OnLoad(DeploymentsState state) =>
        state with { Loading = true, Error = null };

    [ReducerMethod]
    public static DeploymentsState OnLoaded(DeploymentsState state, DeploymentsActions.DeploymentsLoadedAction action) =>
        state with { Candidates = action.Candidates, Loading = false, Error = null, LastFetchedAt = DateTime.UtcNow };

    [ReducerMethod]
    public static DeploymentsState OnLoadFailed(DeploymentsState state, DeploymentsActions.DeploymentsLoadFailedAction action) =>
        state with { Loading = false, Error = action.Error };

    [ReducerMethod]
    public static DeploymentsState OnLoadCommits(DeploymentsState state, DeploymentsActions.LoadCommitsAction action) =>
        state with { CommitsForProjectId = action.ProjectId, CommitsLoading = true, Commits = Array.Empty<CommitRow>() };

    [ReducerMethod]
    public static DeploymentsState OnCommitsLoaded(DeploymentsState state, DeploymentsActions.CommitsLoadedAction action) =>
        action.ProjectId == state.CommitsForProjectId
            ? state with { Commits = action.Commits, CommitsLoading = false }
            : state;

    [ReducerMethod]
    public static DeploymentsState OnRequestFailed(DeploymentsState state, DeploymentsActions.RequestDeploymentFailedAction action) =>
        state with { ActionError = action.Error };

    [ReducerMethod]
    public static DeploymentsState OnApproveBlocked(DeploymentsState state, DeploymentsActions.ApproveDeploymentBlockedAction action) =>
        state with { PendingInFlightWarning = new InFlightWarning(action.Id, action.Message) };

    [ReducerMethod]
    public static DeploymentsState OnApproveFailed(DeploymentsState state, DeploymentsActions.ApproveDeploymentFailedAction action) =>
        state with { ActionError = action.Error };

    [ReducerMethod(typeof(DeploymentsActions.ClearActionErrorAction))]
    public static DeploymentsState OnClearActionError(DeploymentsState state) =>
        state with { ActionError = null, PendingInFlightWarning = null };
}
