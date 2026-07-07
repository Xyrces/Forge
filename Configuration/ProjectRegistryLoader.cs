using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Configuration;

/// <summary>
/// Loads the active registry of projects. Back-compat: a legacy
/// <c>workspace.root</c> with no explicit <c>projects[]</c> array
/// produces a single synthetic project id <c>"default"</c>.
/// </summary>
public static class ProjectRegistryLoader
{
    public static IReadOnlyList<ProjectOptions> Load(
        AgentOptions options,
        ILogger? logger = null,
        IReadOnlyDictionary<string, string>? envOverrides = null)
    {
        envOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projects = options.Projects.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Root))
            .ToList();

        if (projects.Count > 0)
            return projects;

        var legacyRoot = options.Workspace.Root;
        var envOverride = envOverrides.TryGetValue("FORGE_DEFAULT_PROJECT_ROOT", out var r) ? r : null;
        var resolvedRoot = !string.IsNullOrWhiteSpace(envOverride) ? envOverride : legacyRoot;
        if (string.IsNullOrWhiteSpace(resolvedRoot))
        {
            logger?.LogWarning(
                "No projects configured and no legacy workspace.root; registry is empty.");
            return Array.Empty<ProjectOptions>();
        }

        logger?.LogWarning(
            "Legacy workspace.root='{Root}' detected; synthesizing single project id='default'. Migrate to projects[] to silence this warning and enable multi-project concurrency caps.",
            resolvedRoot);

        return new List<ProjectOptions>
        {
            new()
            {
                Id = "default",
                Name = "Default",
                Root = resolvedRoot,
            },
        };
    }
}
