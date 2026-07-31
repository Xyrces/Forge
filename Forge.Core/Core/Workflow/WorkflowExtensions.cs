namespace Forge.Core.Workflow;

/// <summary>
/// Read helpers over a resolved definition for machinery that
/// honors structural edits (pass 4): step enable/disable and
/// bounded branch selections.
/// </summary>
public static class WorkflowExtensions
{
    /// <summary>A step that is absent or disabled is OFF. Machinery
    /// treats "unknown" as off (fail-safe: nothing runs for a step
    /// the definition doesn't declare).</summary>
    public static bool IsStepEnabled(this WorkflowDefinition definition, string stepId)
        => definition.Steps.Any(s => string.Equals(s.Id, stepId, StringComparison.Ordinal) && s.Enabled);

    /// <summary>The active selection for a branch edge, or
    /// <paramref name="fallback"/> when the edge is absent or
    /// declares no options. A selection outside the edge's option
    /// catalog is ignored (defense against hand-edited keys —
    /// publish validation is the real gate).</summary>
    public static string GetEdgeSelection(
        this WorkflowDefinition definition, string from, string to, string fallback)
    {
        var edge = definition.Edges.FirstOrDefault(e =>
            string.Equals(e.From, from, StringComparison.Ordinal)
            && string.Equals(e.To, to, StringComparison.Ordinal));
        if (edge?.Options is null || edge.Options.Count == 0 || edge.Selected is null)
        {
            return fallback;
        }
        return edge.Options.Contains(edge.Selected, StringComparer.Ordinal) ? edge.Selected : fallback;
    }
}
