using Fluxor;

namespace Forge.Dashboard.Features.Projects;

public class ProjectsEffects : Effect<ProjectsActions.LoadProjectsAction>
{
    private readonly ProjectsClient _client;
    public ProjectsEffects(ProjectsClient client) { _client = client; }

    public override async Task HandleAsync(
        ProjectsActions.LoadProjectsAction action,
        IDispatcher dispatcher)
    {
        try
        {
            var rows = await _client.ListAsync(CancellationToken.None);
            dispatcher.Dispatch(new ProjectsActions.ProjectsLoadedAction(rows));
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new ProjectsActions.ProjectsLoadFailedAction(ex.Message));
        }
    }
}

public class UpdateProjectRolesEffect : Effect<ProjectsActions.UpdateProjectRolesAction>
{
    private readonly ProjectsClient _client;
    public UpdateProjectRolesEffect(ProjectsClient client) { _client = client; }

    public override async Task HandleAsync(
        ProjectsActions.UpdateProjectRolesAction action,
        IDispatcher dispatcher)
    {
        dispatcher.Dispatch(new ProjectsActions.UpdateProjectRolesSavingAction());
        try
        {
            await _client.UpdateRolesAsync(action.Id, action.Roles, CancellationToken.None);
            dispatcher.Dispatch(new ProjectsActions.UpdateProjectRolesSucceededAction(action.Id));
            // Reload so the counters, slot meters, and role caps all
            // reflect the persisted + live-applied change.
            dispatcher.Dispatch(new ProjectsActions.LoadProjectsAction());
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new ProjectsActions.UpdateProjectRolesFailedAction(ex.Message));
        }
    }
}

public class AddProjectEffect : Effect<ProjectsActions.AddProjectAction>
{
    private readonly ProjectsClient _client;
    public AddProjectEffect(ProjectsClient client) { _client = client; }

    public override async Task HandleAsync(
        ProjectsActions.AddProjectAction action,
        IDispatcher dispatcher)
    {
        dispatcher.Dispatch(new ProjectsActions.AddProjectSubmittingAction());
        try
        {
            var body = new AddProjectRequestBody(action.Id, action.Name, action.RepoUrl, action.DefaultBranch);
            var resp = await _client.AddAsync(body, CancellationToken.None);
            if (!string.IsNullOrEmpty(resp.Warning))
            {
                // Inline clone failed but the project was registered.
                // Surface the warning so the operator can fix + retry
                // via POST /api/projects/{id}/sync.
                dispatcher.Dispatch(new ProjectsActions.AddProjectFailedAction(
                    $"registered, but clone failed: {resp.Warning}"));
            }
            else
            {
                dispatcher.Dispatch(new ProjectsActions.AddProjectSucceededAction(action.Id));
            }
            // Refresh the list regardless of clone outcome.
            dispatcher.Dispatch(new ProjectsActions.LoadProjectsAction());
            // Refresh the list regardless of clone outcome.
            dispatcher.Dispatch(new ProjectsActions.LoadProjectsAction());
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new ProjectsActions.AddProjectFailedAction(ex.Message));
        }
    }
}
