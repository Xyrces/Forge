using Forge.Core.Workflow;

namespace Forge.Core.Control;

/// <summary>Persist a downgrade fence using the merge gate understood by the prior controller.</summary>
public sealed class DeploymentMergeFence(MemoryStore memory)
{
    public const string AuditKey = "deployment/merge-fence";
    public const string LegacyKey = "gate/merge";
    public const string OperatorKey = "review/operator-merge-gate";

    public static bool LegacyWorkflowHonorsFence(WorkflowDefinition workflow)
        => workflow.Steps.Any(step => step.Enabled && step.Gates.Contains(StageGates.Merge, StringComparer.OrdinalIgnoreCase));

    public async Task HoldAsync(string operation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("A release operation is required.", nameof(operation));
        if (!LegacyWorkflowHonorsFence(await new WorkflowResolver(memory).ResolveAsync(ct)))
            throw new InvalidOperationException("The workflow disables the merge gate; an older controller cannot be safely reactivated.");
        await memory.WriteMergeGateAsync(null, operation, ct);
        if ((await memory.GetAsync(LegacyKey, ct))?.Body != "hold"
            || (await memory.GetAsync(AuditKey, ct))?.Body != operation)
            throw new InvalidOperationException("The persisted deployment merge fence could not be verified.");
    }
}
