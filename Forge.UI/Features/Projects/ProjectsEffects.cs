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
