using System.Text.Json;
using Forge.Configuration;
using Forge.Core;
using Microsoft.Extensions.Logging;

namespace Forge.Agents.Gates;

/// <summary>
/// Ordered evaluation of the gates registered for a checkpoint.
/// Resolution order (first non-empty wins):
/// <list type="number">
/// <item>DB override: memory key <c>gates/run/&lt;checkpoint&gt;</c>
/// (JSON string array) — the future UI writes these.</item>
/// <item>Config: <c>gates.run.&lt;checkpoint&gt;</c> in appsettings.</item>
/// <item>Built-in defaults (<see cref="Defaults"/>).</item>
/// </list>
/// Unknown gate names are skipped with a warning (a stale DB row
/// must not brick the pipeline).
/// </summary>
public sealed class RunGatePipeline
{
    public const string PreImplementationCheckpoint = "preImplementation";

    /// <summary>Built-in default gate order per checkpoint. The
    /// deterministic gates front-run the LLM critic so the expensive
    /// call only sees well-formed, in-territory plans.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Defaults =
        new Dictionary<string, string[]>
        {
            [PreImplementationCheckpoint] = new[] { PlanSchemaGate.GateName, PlanTerritoryGate.GateName, PlanLlmReviewGate.GateName },
        };

    /// <summary>Known-gate catalog keyed by name, for the read-only
    /// catalog endpoint. Each entry carries the kind (Deterministic
    /// or Llm) and a one-line description sourced from the gate
    /// class itself.</summary>
    public static readonly IReadOnlyDictionary<string, (GateKind Kind, string Description)> GateCatalog =
        new Dictionary<string, (GateKind Kind, string Description)>
        {
            [PlanSchemaGate.GateName] = (GateKind.Deterministic, PlanSchemaGate.DescriptionText),
            [PlanTerritoryGate.GateName] = (GateKind.Deterministic, PlanTerritoryGate.DescriptionText),
            [PlanLlmReviewGate.GateName] = (GateKind.Llm, PlanLlmReviewGate.DescriptionText),
        };

    private readonly GateOptions _options;
    private readonly MemoryStore? _memory;
    private readonly Func<string, IRunGate?> _gateFactory;
    private readonly ILogger _logger;

    public RunGatePipeline(
        GateOptions options,
        MemoryStore? memory,
        Func<string, IRunGate?> gateFactory,
        ILogger logger)
    {
        _options = options;
        _memory = memory;
        _gateFactory = gateFactory;
        _logger = logger;
    }

    /// <summary>Resolve the ordered gate list for a checkpoint:
    /// DB override -> config -> built-in defaults.</summary>
    public async Task<IReadOnlyList<string>> ResolveGateNamesAsync(string checkpoint, CancellationToken ct)
    {
        if (_memory is not null)
        {
            var rows = await _memory.RecallAsync($"gates/run/{checkpoint}", ct);
            var row = rows.LastOrDefault();
            if (row is not null)
            {
                try
                {
                    var names = JsonSerializer.Deserialize<string[]>(row.Body);
                    if (names is { Length: > 0 }) return names;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "RunGatePipeline: malformed DB override gates/run/{Checkpoint} — falling through to config", checkpoint);
                }
            }
        }
        if (_options.Run.TryGetValue(checkpoint, out var configured) && configured.Length > 0)
        {
            return configured;
        }
        return Defaults.TryGetValue(checkpoint, out var builtIn) ? builtIn : Array.Empty<string>();
    }

    /// <summary>
    /// Resolve the ordered gate list for a checkpoint AND
    /// identify the resolution source ("db_override", "config",
    /// or "builtin_default"). Used by the read-only catalog
    /// endpoint so the operator can see where the gate list
    /// comes from.
    /// </summary>
    public async Task<(IReadOnlyList<string> Names, string Source)> ResolveWithSourceAsync(
        string checkpoint, CancellationToken ct)
    {
        // Check DB override first.
        if (_memory is not null)
        {
            var rows = await _memory.RecallAsync($"gates/run/{checkpoint}", ct);
            var row = rows.LastOrDefault();
            if (row is not null)
            {
                try
                {
                    var names = JsonSerializer.Deserialize<string[]>(row.Body);
                    if (names is { Length: > 0 }) return (names, "db_override");
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "RunGatePipeline: malformed DB override gates/run/{Checkpoint} — falling through to config", checkpoint);
                }
            }
        }
        // Check config.
        if (_options.Run.TryGetValue(checkpoint, out var configured) && configured.Length > 0)
        {
            return (configured, "config");
        }
        // Fall back to built-in defaults.
        if (Defaults.TryGetValue(checkpoint, out var builtIn))
        {
            return (builtIn, "builtin_default");
        }
        return (Array.Empty<string>(), "unknown");
    }

    /// <summary>Evaluate the checkpoint's gates in order. First
    /// non-Approve short-circuits; every verdict is appended to
    /// <paramref name="state"/> for the audit trail.</summary>
    public async Task<RunGateVerdict> EvaluateAsync(
        string checkpoint, RunGateContext ctx, RunGateState state)
    {
        var names = await ResolveGateNamesAsync(checkpoint, ctx.Ct);
        if (names.Count == 0)
        {
            return RunGateVerdict.Approved;   // gates removed entirely = open pipeline (operator choice)
        }
        foreach (var name in names)
        {
            var gate = _gateFactory(name);
            if (gate is null)
            {
                _logger.LogWarning("RunGatePipeline: unknown gate '{Name}' for {Checkpoint} — skipped", name, checkpoint);
                continue;
            }
            RunGateVerdict verdict;
            try
            {
                verdict = await gate.EvaluateAsync(ctx);
            }
            catch (Exception ex)
            {
                // A gate that throws must not brick the pipeline —
                // treat as approve-with-warning and log loudly.
                _logger.LogError(ex, "RunGatePipeline: gate '{Name}' threw — approving with warning", name);
                verdict = new RunGateVerdict(GateOutcome.Approve, $"gate error (approved with warning): {ex.GetType().Name}");
            }
            state.Verdicts.Add((gate.Name, verdict.Outcome, verdict.Feedback));
            if (verdict.Outcome != GateOutcome.Approve)
            {
                return verdict;
            }
        }
        return RunGateVerdict.Approved;
    }
}
