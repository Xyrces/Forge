using System.Text.RegularExpressions;

namespace Forge.Configuration;

/// <summary>Validates deployment policy and captures physical destinations before approval.</summary>
public static class DeploymentEnvironmentResolver
{
    private static bool IsName(string? value) => value is not null && Regex.IsMatch(value, "^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$");

    public static void Validate(ProjectOptions project)
    {
        var deployment = project.Deployment ?? throw new InvalidOperationException("Deployment configuration is missing.");
        if (deployment.Environments is null)
            throw new InvalidOperationException("Deployment environments must be a dictionary.");
        if (deployment.Environments.Count == 0)
        {
            if (deployment.SprintEnvironment is not null)
                throw new InvalidOperationException("Sprint environment must name a configured environment.");
            return;
        }
        if (deployment.Kind != DeploymentKind.Kubernetes)
            throw new InvalidOperationException("Named environments currently require Kubernetes deployment.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var datasets = new HashSet<Guid>();
        foreach (var (name, environment) in deployment.Environments)
        {
            if (!IsName(name) || !names.Add(name))
                throw new InvalidOperationException("Environment names must be unique lowercase DNS labels.");
            if (environment is null || string.IsNullOrWhiteSpace(environment.DisplayName) || environment.DisplayName.Length > 100)
                throw new InvalidOperationException($"Environment '{name}' needs a display name of at most 100 characters.");
            if (!IsName(environment.KubernetesNamespace) || !namespaces.Add(environment.KubernetesNamespace))
                throw new InvalidOperationException("Each environment requires its own valid Kubernetes namespace.");
            if (!Guid.TryParse(environment.KubernetesDatasetId, out var dataset) || !datasets.Add(dataset))
                throw new InvalidOperationException("Each environment requires its own valid dataset identifier.");
        }
        if (deployment.SprintEnvironment is not null && !names.Contains(deployment.SprintEnvironment))
            throw new InvalidOperationException("Sprint environment must name a configured environment.");
        foreach (var (name, environment) in deployment.Environments)
        {
            var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (environment.PromoteTo is null)
                throw new InvalidOperationException($"Environment '{name}' promotion routes must be a list.");
            foreach (var target in environment.PromoteTo)
                if (!names.Contains(target) || string.Equals(name, target, StringComparison.OrdinalIgnoreCase) || !routes.Add(target))
                    throw new InvalidOperationException($"Environment '{name}' has an invalid promotion route.");
        }
    }

    public static DeploymentTargetSnapshot Capture(ProjectOptions project, string name)
    {
        Validate(project);
        var entry = project.Deployment!.Environments.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase));
        if (entry.Value is null) throw new InvalidOperationException($"Deployment environment '{name}' is not configured.");
        return new(entry.Key, entry.Value.DisplayName, entry.Value.KubernetesNamespace, entry.Value.KubernetesDatasetId);
    }

    public static ProjectOptions Apply(ProjectOptions project, DeploymentTargetSnapshot snapshot)
    {
        var deployment = project.Deployment ?? throw new InvalidOperationException("Deployment configuration is missing.");
        if (deployment.Kind != DeploymentKind.Kubernetes || !IsName(snapshot.Name) || !IsName(snapshot.KubernetesNamespace) ||
            !Guid.TryParse(snapshot.KubernetesDatasetId, out _))
            throw new InvalidOperationException("The captured deployment target is invalid.");
        // Deliberately use the approved snapshot, not current settings: edits must
        // never redirect reconciliation or rollback to another environment.
        return project with { Deployment = deployment with {
            KubernetesNamespace = snapshot.KubernetesNamespace,
            KubernetesDatasetId = snapshot.KubernetesDatasetId } };
    }
}
