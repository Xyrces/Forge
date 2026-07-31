namespace Forge.Agents;

/// <summary>
/// Resolves the directory role prompt files (<c>&lt;role&gt;.md</c>)
/// are loaded from. A per-project override wins
/// (<c>&lt;projectRoot&gt;/agents</c>); otherwise the orchestrator's
/// built-in defaults that ship next to the app
/// (<c>&lt;appBase&gt;/agents</c>, copied from the repo's
/// <c>agents/</c> at publish time). Without this fallback every
/// non-Forge project silently ran with degraded "You are the X
/// agent" instructions because its clone has no <c>agents/</c> dir.
/// </summary>
public static class RolePromptRoot
{
    public static string Resolve(string projectRoot, string? appBaseDirectory = null)
    {
        var perProject = Path.Combine(projectRoot, "agents");
        if (Directory.Exists(perProject)) return perProject;
        return Path.Combine(appBaseDirectory ?? AppContext.BaseDirectory, "agents");
    }
}
