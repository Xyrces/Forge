namespace Forge.Projects;

/// <summary>Qualify project storage before making the project visible in the registry.</summary>
public interface IProjectProvisioner
{
    Task EnsureReadyAsync(string projectId, CancellationToken ct);
}
