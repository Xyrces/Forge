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
}
