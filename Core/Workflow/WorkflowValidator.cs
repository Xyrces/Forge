namespace Forge.Core.Workflow;

/// <summary>
/// Publish-time validation for workflow definitions (fail closed,
/// operator-readable errors). Because edges are descriptive and the
/// transition table is code-owned, the catalog of known step ids,
/// gate names, policy keys, and allowed values is exactly what the
/// existing machinery knows how to honor — a definition outside
/// that catalog can never reach the live key.
/// </summary>
public static class WorkflowValidator
{
    private static readonly string[] NoDiffOutcomes = { "completed", "rework" };

    public static IReadOnlyList<string> Validate(WorkflowDefinition d)
    {
        var errors = new List<string>();
        var catalog = WorkflowDefaults.Definition.Steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        if (d.Steps.Count == 0)
        {
            errors.Add("definition has no steps");
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in d.Steps)
        {
            if (string.IsNullOrWhiteSpace(s.Id))
            {
                errors.Add("a step has an empty id");
                continue;
            }
            if (!seen.Add(s.Id))
            {
                errors.Add($"duplicate step id '{s.Id}'");
            }
            if (!catalog.Contains(s.Id))
            {
                errors.Add($"unknown step '{s.Id}' — the machinery only knows the built-in catalog");
            }
            present.Add(s.Id);
            if (!s.Enabled && !s.Optional)
            {
                errors.Add($"step '{s.Id}' is not optional and cannot be disabled");
            }
            foreach (var g in s.Gates)
            {
                if (!StageGates.IsKnown(g))
                {
                    errors.Add($"unknown gate '{g}' on step '{s.Id}'");
                }
            }
        }

        foreach (var e in d.Edges)
        {
            if (!present.Contains(e.From))
            {
                errors.Add($"edge references unknown step '{e.From}'");
            }
            if (!present.Contains(e.To))
            {
                errors.Add($"edge references unknown step '{e.To}'");
            }
        }

        foreach (var (key, value) in d.Policies)
        {
            switch (key)
            {
                case WorkflowPolicies.MaxStrikes:
                    if (!int.TryParse(value, out var ms) || ms < 1 || ms > 10)
                    {
                        errors.Add($"policy '{key}' must be an integer 1-10 (got '{value}')");
                    }
                    break;
                case WorkflowPolicies.StallGraceMinutes:
                    if (!int.TryParse(value, out var sg) || sg < 1 || sg > 1440)
                    {
                        errors.Add($"policy '{key}' must be an integer 1-1440 (got '{value}')");
                    }
                    break;
                case WorkflowPolicies.ParkOnInfra:
                case WorkflowPolicies.AutoMerge:
                    if (value is not ("true" or "false"))
                    {
                        errors.Add($"policy '{key}' must be 'true' or 'false' (got '{value}')");
                    }
                    break;
                case WorkflowPolicies.NoDiffOutcome:
                    if (!NoDiffOutcomes.Contains(value, StringComparer.Ordinal))
                    {
                        errors.Add($"policy '{key}' must be one of {string.Join("/", NoDiffOutcomes)} (got '{value}')");
                    }
                    break;
                default:
                    errors.Add($"unknown policy '{key}'");
                    break;
            }
        }

        return errors;
    }

    /// <summary>Human-readable diff between two definitions (draft
    /// vs live), one line per change. Used by the editor's publish
    /// preview and the audit event.</summary>
    public static IReadOnlyList<string> Diff(WorkflowDefinition from, WorkflowDefinition to)
    {
        var lines = new List<string>();
        var fromSteps = from.Steps.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var toSteps = to.Steps.ToDictionary(s => s.Id, StringComparer.Ordinal);

        foreach (var (id, ts) in toSteps)
        {
            if (!fromSteps.TryGetValue(id, out var fs))
            {
                lines.Add($"step '{id}' added");
                continue;
            }
            if (fs.Enabled != ts.Enabled)
            {
                lines.Add($"step '{id}' {(ts.Enabled ? "enabled" : "disabled")}");
            }
            var detached = fs.Gates.Except(ts.Gates, StringComparer.Ordinal);
            var attached = ts.Gates.Except(fs.Gates, StringComparer.Ordinal);
            foreach (var g in attached) lines.Add($"gate '{g}' attached to '{id}'");
            foreach (var g in detached) lines.Add($"gate '{g}' detached from '{id}'");
            if (!string.Equals(fs.Label, ts.Label, StringComparison.Ordinal))
            {
                lines.Add($"step '{id}' relabeled '{fs.Label}' → '{ts.Label}'");
            }
        }
        foreach (var id in fromSteps.Keys.Except(toSteps.Keys, StringComparer.Ordinal))
        {
            lines.Add($"step '{id}' removed");
        }

        var keys = from.Policies.Keys.Union(to.Policies.Keys, StringComparer.Ordinal);
        foreach (var k in keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            from.Policies.TryGetValue(k, out var fv);
            to.Policies.TryGetValue(k, out var tv);
            if (!string.Equals(fv, tv, StringComparison.Ordinal))
            {
                lines.Add($"policy '{k}': {fv ?? "—"} → {tv ?? "—"}");
            }
        }
        return lines;
    }
}
