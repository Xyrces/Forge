using Fluxor;

namespace Forge.Dashboard.Features.Projects;

public static class ProjectsReducers
{
    [ReducerMethod(typeof(ProjectsActions.LoadProjectsAction))]
    public static ProjectsState OnLoad(ProjectsState state) =>
        state with { Loading = true, Error = null };

    [ReducerMethod]
    public static ProjectsState OnLoaded(ProjectsState state, ProjectsActions.ProjectsLoadedAction action) =>
        state with { Projects = action.Projects, Loading = false, Error = null, LastFetchedAt = DateTime.UtcNow };

    [ReducerMethod]
    public static ProjectsState OnFailed(ProjectsState state, ProjectsActions.ProjectsLoadFailedAction action) =>
        state with { Loading = false, Error = action.Error };

    [ReducerMethod(typeof(ProjectsActions.AddProjectSubmittingAction))]
    public static ProjectsState OnSubmitting(ProjectsState state) =>
        state with { Submitting = true, AddError = null };

    [ReducerMethod]
    public static ProjectsState OnAddSucceeded(ProjectsState state, ProjectsActions.AddProjectSucceededAction action) =>
        state with
        {
            Submitting = false,
            AddError = null,
            LastAdded = action.Id,
        };

    [ReducerMethod]
    public static ProjectsState OnAddFailed(ProjectsState state, ProjectsActions.AddProjectFailedAction action) =>
        state with { Submitting = false, AddError = action.Error };

    [ReducerMethod(typeof(ProjectsActions.AddProjectDismissErrorAction))]
    public static ProjectsState OnDismissAddError(ProjectsState state) =>
        state with { AddError = null };
}
