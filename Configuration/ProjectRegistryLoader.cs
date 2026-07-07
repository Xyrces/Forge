using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Configuration;

/// <summary>
/// Loads the active registry of projects. Three-tier resolution:
///   1. <c>projects[]</c> in <see cref="AgentOptions.Projects"/> (operator-chosen).
///   2. Legacy <c>workspace.root</c> + env override
///      (<c>FORGE_DEFAULT_PROJECT_ROOT</c>) — synthesizes id="default".
///   3. Both empty — synthesizes a single project id="default" whose
///      Root will be assigned at bootstrap time under
///      <see cref="ForgesystemPaths.ProjectDir"/>; this is the
///      "zero-config fresh machine" path.
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
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .ToList();

        if (projects.Count > 0)
            return projects;

        var legacyRoot = options.Workspace.Root;
        var envOverride = envOverrides.TryGetValue("FORGE_DEFAULT_PROJECT_ROOT", out var r) ? r : null;
        var resolvedRoot = !string.IsNullOrWhiteSpace(envOverride) ? envOverride : legacyRoot;

        if (string.IsNullOrWhiteSpace(resolvedRoot))
        {
            logger?.LogInformation(
                "No projects[] configured and no legacy workspace.root; will auto-scaffold a single id='default' project under the Forgesystem data root.");
            return new List<ProjectOptions>
            {
                new()
                {
                    Id = "default",
                    Name = "Default",
                    Root = string.Empty,
                },
            };
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
