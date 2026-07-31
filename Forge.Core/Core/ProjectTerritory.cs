namespace Forge.Core;

/// <summary>
/// Per-project territory override for a role: the repo-relative path
/// prefixes the role's plans may touch, plus whether bare repo-root
/// files are allowed. Persisted in <c>project.roles_json</c> under the
/// reserved <c>$territory</c> key alongside the role caps (the JSON
/// column predates territory; no schema migration). A project with no
/// entry for a role falls back to the built-in
/// <c>RoleAgentRegistry</c> territory — which is Forge-repo-shaped, so
/// any project whose repo layout differs must define its own.
/// </summary>
public sealed record RoleTerritory(
    IReadOnlyList<string> Prefixes,
    bool AllowsRootFiles);
