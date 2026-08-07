namespace Forge.Core;

/// <summary>
/// Storage contract for the inter-sprint build-state snapshot
/// (operator request 2026-08-06). The SprintAssembler (Orchestrator)
/// writes a JSON snapshot to the project's memory store under
/// <see cref="BuildStateKey"/> every tick; GET /api/sprints/building
/// (Dashboard) reads it back. The key lives in Core because it is a
/// cross-module storage contract — Dashboard must not reference
/// Orchestrator (module boundary).
/// </summary>
public static class SprintBuildStateKeys
{
    public const string BuildStateKey = "sprint/build";
}
