namespace Forge.Agents.Gates;

/// <summary>
/// Deterministic plan-structure gate: the plan must contain the
/// required sections so later gates (and the LLM critic) can judge
/// substance, and so the agent actually thought each dimension
/// through. Zero LLM.
/// </summary>
public sealed class PlanSchemaGate : IRunGate
{
    public const string GateName = "plan-schema";
    public string Name => GateName;
    public GateKind Kind => GateKind.Deterministic;
    public string Description => DescriptionText;

    /// <summary>User-facing description of this gate for the catalog.</summary>
    public const string DescriptionText =
        "Checks that the plan contains the required sections (goal, files, approach, test, done).";

    /// <summary>Required sections, matched as case-insensitive
    /// keywords at the start of a line (markdown heading or bold
    /// label both satisfy: "## Files", "Files:", "**Files**").</summary>
    private static readonly (string Key, string Hint)[] Required =
    {
        ("goal", "what the change achieves, restated in your own words"),
        ("files", "the concrete file paths you will create or modify"),
        ("approach", "how you will make the change"),
        ("test", "how you will prove it (tests you will add/run)"),
        ("done", "the evidence that will show the task is complete"),
    };

    public Task<RunGateVerdict> EvaluateAsync(RunGateContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Plan) || ctx.Plan.Trim().Length < 100)
        {
            return Task.FromResult(new RunGateVerdict(GateOutcome.Revise,
                "Plan is too thin. Submit a structured plan with these sections: " +
                string.Join("; ", Required.Select(r => $"{r.Key} ({r.Hint})")) + "."));
        }
        var missing = Required
            .Where(r => !ContainsSection(ctx.Plan, r.Key))
            .Select(r => $"{r.Key} ({r.Hint})")
            .ToList();
        if (missing.Count > 0)
        {
            return Task.FromResult(new RunGateVerdict(GateOutcome.Revise,
                "Plan is missing required section(s): " + string.Join("; ", missing) +
                ". Add them and resubmit."));
        }
        return Task.FromResult(RunGateVerdict.Approved);
    }

    private static bool ContainsSection(string plan, string key)
    {
        foreach (var line in plan.Split('\n'))
        {
            var t = line.Trim().TrimStart('#', '*', '-', ' ').TrimEnd();
            if (t.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                && (t.Length == key.Length || t[key.Length] is ':' or '*' or ' ' or '—' or '-'))
            {
                return true;
            }
        }
        return false;
    }
}
