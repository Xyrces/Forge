using Forge.Core;
using Forge.Core.Workflow;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Publish-time validation + draft/live diff for the editable
/// workflow, and the StageGates wiring semantic (a gate attached
/// nowhere in the resolved definition is disabled).
/// </summary>
public sealed class WorkflowValidatorTests : IDisposable
{
    private readonly string _workDir;

    public WorkflowValidatorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "forge-wfv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private MemoryStore NewMemory()
    {
        var path = Path.Combine(_workDir, Guid.NewGuid().ToString("N") + ".db");
        var bootstrap = new IssueStore(path);
        bootstrap.Dispose();
        return new MemoryStore(path);
    }

    [Fact]
    public void Validate_DefaultDefinition_IsClean()
        => Assert.Empty(WorkflowValidator.Validate(WorkflowDefaults.Definition));

    [Fact]
    public void Validate_UnknownStep_Rejected()
    {
        var d = WithSteps(s => s.Add(new WorkflowStep("teleport", "Teleport", "implementation", "stage", false, true, 0, 0, Array.Empty<string>())));
        Assert.Contains(WorkflowValidator.Validate(d), e => e.Contains("unknown step 'teleport'"));
    }

    [Fact]
    public void Validate_DisableNonOptionalStep_Rejected()
    {
        var d = WithSteps(s =>
        {
            var i = s.FindIndex(x => x.Id == "agent");
            s[i] = s[i] with { Enabled = false };
        });
        Assert.Contains(WorkflowValidator.Validate(d), e => e.Contains("'agent' is not optional"));
    }

    [Fact]
    public void Validate_DisableOptionalStep_Allowed()
    {
        var d = WithSteps(s =>
        {
            var i = s.FindIndex(x => x.Id == "design");
            s[i] = s[i] with { Enabled = false };
        });
        Assert.Empty(WorkflowValidator.Validate(d));
    }

    [Fact]
    public void Validate_PolicyRanges_Enforced()
    {
        var bad = WorkflowDefaults.Definition with
        {
            Policies = new Dictionary<string, string>(WorkflowDefaults.Definition.Policies)
            {
                [WorkflowPolicies.MaxStrikes] = "0",
                [WorkflowPolicies.StallGraceMinutes] = "99999",
                [WorkflowPolicies.AutoMerge] = "yes",
                [WorkflowPolicies.NoDiffOutcome] = "explode",
                ["madeUp"] = "1",
            },
        };
        var errors = WorkflowValidator.Validate(bad);
        Assert.Contains(errors, e => e.Contains(WorkflowPolicies.MaxStrikes));
        Assert.Contains(errors, e => e.Contains(WorkflowPolicies.StallGraceMinutes));
        Assert.Contains(errors, e => e.Contains(WorkflowPolicies.AutoMerge));
        Assert.Contains(errors, e => e.Contains(WorkflowPolicies.NoDiffOutcome));
        Assert.Contains(errors, e => e.Contains("unknown policy 'madeUp'"));
    }

    [Fact]
    public void Validate_UnknownGate_Rejected()
    {
        var d = WithSteps(s =>
        {
            var i = s.FindIndex(x => x.Id == "review");
            s[i] = s[i] with { Gates = new[] { "merge", "purple" } };
        });
        Assert.Contains(WorkflowValidator.Validate(d), e => e.Contains("unknown gate 'purple'"));
    }

    [Fact]
    public void Validate_EdgeToUnknownStep_Rejected()
    {
        var d = WorkflowDefaults.Definition with
        {
            Edges = WorkflowDefaults.Definition.Edges
                .Append(new WorkflowEdge("pr", "nowhere", "branch", null, null)).ToList(),
        };
        Assert.Contains(WorkflowValidator.Validate(d), e => e.Contains("unknown step 'nowhere'"));
    }

    [Fact]
    public void Validate_DuplicateStep_Rejected()
    {
        var d = WithSteps(s => s.Add(s.First(x => x.Id == "groom")));
        Assert.Contains(WorkflowValidator.Validate(d), e => e.Contains("duplicate step id 'groom'"));
    }

    [Fact]
    public void Diff_GateDetach_Reported()
    {
        var draft = WithSteps(s =>
        {
            var i = s.FindIndex(x => x.Id == "review");
            s[i] = s[i] with { Gates = Array.Empty<string>() };
        });
        Assert.Contains(WorkflowValidator.Diff(WorkflowDefaults.Definition, draft),
            l => l == "gate 'merge' detached from 'review'");
    }

    [Fact]
    public void Diff_PolicyChange_Reported()
    {
        var draft = WorkflowDefaults.Definition with
        {
            Policies = new Dictionary<string, string>(WorkflowDefaults.Definition.Policies)
            {
                [WorkflowPolicies.MaxStrikes] = "5",
            },
        };
        Assert.Contains(WorkflowValidator.Diff(WorkflowDefaults.Definition, draft),
            l => l.Contains(WorkflowPolicies.MaxStrikes) && l.Contains("3") && l.Contains("5"));
    }

    [Fact]
    public void Diff_NoChange_Empty()
        => Assert.Empty(WorkflowValidator.Diff(WorkflowDefaults.Definition, WorkflowDefaults.Definition));

    [Fact]
    public async Task StageGates_DetachedGate_HoldHasNoEffect()
    {
        // The wiring semantic behind gate detach: a gate attached
        // nowhere in the resolved definition is DISABLED — holding it
        // changes nothing. Default definition attaches all four.
        var memory = NewMemory();
        var resolver = new WorkflowResolver(memory);
        var gates = new StageGates(memory, resolver);
        await gates.HoldAsync(StageGates.Merge);
        Assert.True(await gates.IsHeldAsync(StageGates.Merge));   // attached by default

        var detached = WithSteps(s =>
        {
            var i = s.FindIndex(x => x.Id == "review");
            s[i] = s[i] with { Gates = Array.Empty<string>() };
        });
        await memory.RememberAsync(WorkflowResolver.LiveKey, WorkflowResolver.Serialize(detached));
        Assert.False(await gates.IsHeldAsync(StageGates.Merge));  // detached -> disabled
    }

    [Fact]
    public async Task StageGates_NullResolver_LegacyBehavior()
    {
        var memory = NewMemory();
        var gates = new StageGates(memory);
        await gates.HoldAsync(StageGates.Merge);
        Assert.True(await gates.IsHeldAsync(StageGates.Merge));
    }

    private static WorkflowDefinition WithSteps(Action<List<WorkflowStep>> mutate)
    {
        var steps = WorkflowDefaults.Definition.Steps.ToList();
        mutate(steps);
        return WorkflowDefaults.Definition with { Steps = steps };
    }
}
