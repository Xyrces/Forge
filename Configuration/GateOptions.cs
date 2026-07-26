namespace Forge.Configuration;

/// <summary>
/// Quality-gate configuration: ordered gate names per checkpoint.
/// Resolution order per checkpoint: DB override (memory key
/// <c>gates/run/&lt;checkpoint&gt;</c>, future UI-managed) -> this
/// config -> built-in defaults. Removing a gate here removes it
/// from the pipeline without a code change.
/// </summary>
public sealed class GateOptions
{
    /// <summary>Ordered gate names per checkpoint, e.g.
    /// "preImplementation": ["plan-schema", "plan-territory",
    /// "plan-llm-review"].</summary>
    public Dictionary<string, string[]> Run { get; set; } = new();
}
