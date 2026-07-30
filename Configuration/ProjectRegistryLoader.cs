using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Configuration;

/// <summary>
/// Loads the active registry of projects. The SQLite <c>project</c>
/// table is the ONLY source of truth — runtime-added via
/// <c>POST /api/projects</c> and persisted across restarts. The
/// orchestrator dashboard + dispatch loop reads from the store on
/// each cycle (live mode).
///
/// <para>
/// The legacy <c>workspace.root</c> shim was removed in the
/// "database-only registry" iteration. Operators who previously
/// relied on <c>appsettings.json</c> + <c>workspace.root</c> should
/// use the dashboard Projects page or POST <c>/api/projects</c> to
/// register their target project. Existing projects registered via
/// the appsettings route (and present in SQLite) are picked up
/// automatically — no migration step required.
/// </para>
/// </summary>
public static class ProjectRegistryLoader
{
    public static async Task<IReadOnlyList<ProjectOptions>> LoadAsync(
        IProjectStore store,
        CancellationToken ct = default)
    {
        var rows = await store.ListAsync(ct);
        return rows.Select(ToOptions).ToList();
    }

    /// <summary>
    /// Legacy appsettings.json <c>projects[]</c> loader. Returns an
    /// empty list by default; kept around only so callers don't
    /// break during the migration. The seed step was removed so the
    /// SQLite table is the single source of truth — operators add
    /// projects via the dashboard / API.
    /// </summary>
    public static async Task SeedAsync(IProjectStore store, ProjectsOptions config, ILogger? logger = null, CancellationToken ct = default)
    {
        if (config.Projects.Count == 0) return;
        logger?.LogWarning(
            "appsettings.json `projects[]` is no longer seeded into SQLite. Add projects via the dashboard or POST /api/projects. Ignoring {Count} config entries.",
            config.Projects.Count);
        await Task.CompletedTask;
    }

    private static ProjectOptions ToOptions(ProjectRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        RepoUrl = r.RepoUrl,
        DefaultBranch = r.DefaultBranch,
        // Root is derived at bootstrap time from dataRoot + RepoUrl.
        // ProjectStore doesn't store Root because the SQLite row
        // would drift on operator moves.
        Root = string.Empty,
        // Role caps persist in SQLite (v19 roles_json); the
        // orchestrator seeds SlotTable from these at startup.
        Roles = new Dictionary<string, int>(r.Roles, StringComparer.OrdinalIgnoreCase),
        Territories = new Dictionary<string, Core.RoleTerritory>(r.Territories, StringComparer.OrdinalIgnoreCase),
        VerifyCommands = r.VerifyCommands?.ToList(),
    };
}
