namespace Forge.Core.Workflow;

/// <summary>
/// Typed accessors over a resolved workflow definition's policy
/// dictionary. Every lookup has a code-side fallback (today's
/// constants), so machinery reading policies can never be bricked
/// by a missing key — publish-time validation (<see
/// cref="WorkflowValidator"/>) keeps values in range, and these
/// readers stay defensive anyway.
/// </summary>
public static class WorkflowPolicyReader
{
    public static int GetInt(WorkflowDefinition definition, string key, int fallback)
        => definition.Policies.TryGetValue(key, out var raw) && int.TryParse(raw, out var v)
            ? v
            : fallback;

    public static bool GetBool(WorkflowDefinition definition, string key, bool fallback)
        => definition.Policies.TryGetValue(key, out var raw) && raw is "true" or "false"
            ? raw == "true"
            : fallback;

    public static string GetString(WorkflowDefinition definition, string key, string fallback)
        => definition.Policies.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw
            : fallback;
}
