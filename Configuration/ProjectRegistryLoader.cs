using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Configuration;

/// <summary>
/// Loads the active registry of projects. Sources are merged in this
/// order (later wins on conflict):
///   1. The <c>project</c> table in SQLite (runtime add via
///      <c>POST /api/projects</c>, plus projects copied in from
///      appsettings.json on first boot — see <see cref="SeedAsync"/>).
///   2. <c>projects[]</c> in <see cref="AgentOptions.Projects"/> —
///      the operator-chosen initial list. Mirrored into SQLite on
///      first boot via <see cref="SeedAsync"/>.
///   3. Legacy <c>workspace.root</c> + env override
///      (<c>FORGE_DEFAULT_PROJECT_ROOT</c>) — synthesizes id="default".
///   4. Both empty — synthesizes a single project id="default" whose
///      Root will be assigned at bootstrap time under
///      <see cref="ForgesystemPaths.ProjectDir"/>; this is the
///      "zero-config fresh machine" path.
/// </summary>
public static class ProjectRegistryLoader
{
    public static IReadOnlyList<ProjectOptions> Load(
        AgentOptions options,
        IProjectStore store,
        ILogger? logger = null,
        IReadOnlyDictionary<string, string>? envOverrides = null)
    {
        envOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var configProjects = options.Projects.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .ToList();

        var storedProjects = store.ListAsync(CancellationToken.None)
            .GetAwaiter().GetResult()
            .Select(ToOptions)
            .ToList();

        // SQLite is authoritative for any project it has a row for
        // (the operator could have edited it via the API); config
        // adds projects that aren't yet in SQLite.
        var merged = new Dictionary<string, ProjectOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in storedProjects) merged[p.Id] = p;
        foreach (var p in configProjects)
        {
            if (!merged.ContainsKey(p.Id)) merged[p.Id] = p;
        }

        if (merged.Count > 0) return merged.Values.ToList();

        // No stored or configured projects — fall through to the
        // legacy synthesis path (workspace.root or zero-config).
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

    /// <summary>
    /// One-time copy of appsettings.json <c>projects[]</c> into the
    /// SQLite <c>project</c> table. Idempotent: rows that already
    /// exist for the same id are skipped (operator edits via the API
    /// survive restarts).
    /// </summary>
    public static async Task SeedAsync(IProjectStore store, ProjectsOptions config, ILogger? logger = null, CancellationToken ct = default)
    {
        foreach (var p in config.Projects)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) continue;
            var existing = await store.GetAsync(p.Id, ct);
            if (existing is not null) continue;
            await store.UpsertAsync(new NewProject(
                Id: p.Id,
                Name: p.Name,
                RepoUrl: p.RepoUrl,
                DefaultBranch: string.IsNullOrWhiteSpace(p.DefaultBranch) ? "main" : p.DefaultBranch), ct);
            logger?.LogInformation("Seeded project '{Id}' into SQLite from appsettings.json", p.Id);
        }
    }

    private static ProjectOptions ToOptions(ProjectRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        RepoUrl = r.RepoUrl,
        DefaultBranch = r.DefaultBranch,
        // Root is derived at bootstrap time from dataRoot + RepoUrl
        // (or taken from the operator's appsettings). ProjectStore
        // doesn't store Root because the SQLite row would drift on
        // operator moves.
        Root = string.Empty,
    };
}
