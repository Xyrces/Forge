using Forge.Core;
using Forge.Core.Control;
using Forge.Core.Workflow;
using Xunit;
namespace Forge.Tests;

public sealed class DeploymentMergeFenceTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("open", false)]
    [InlineData("hold", true)]
    public async Task FencePreservesOperatorIntentAndRemainsVisibleToPriorController(string? previous, bool operatorHeld)
    {
        var directory = TempRoot.Instance.NewDirectory("merge-fence");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        await using var issues = new IssueStore(path);
        await using (var memory = new MemoryStore(path))
        {
            if (previous is not null) await memory.RememberAsync("gate/merge", previous);
            await new DeploymentMergeFence(memory).HoldAsync("upgrade-one");
        }
        await using var reopened = new MemoryStore(path);
        var gates = new StageGates(reopened, new WorkflowResolver(reopened));
        // The previous production binary resolves the first prefix-matched row.
        async Task<string> LegacyValue() => (await reopened.RecallAsync("gate/merge")).First().Body;
        Assert.Equal("hold", await LegacyValue());
        Assert.Equal(operatorHeld, await gates.IsHeldAsync(StageGates.Merge));
        await gates.HoldAsync(StageGates.Merge);
        Assert.True(await gates.IsHeldAsync(StageGates.Merge));
        Assert.Equal("hold", await LegacyValue());
        await gates.ReleaseAsync(StageGates.Merge);
        Assert.False(await gates.IsHeldAsync(StageGates.Merge));
        Assert.Equal("hold", await LegacyValue());
        await new DeploymentMergeFence(reopened).HoldAsync("upgrade-one");
        Assert.False(await gates.IsHeldAsync(StageGates.Merge));
        Assert.Equal("hold", await LegacyValue());
        Assert.Equal("upgrade-one", (await reopened.GetAsync(DeploymentMergeFence.AuditKey))?.Body);
    }

    [Fact]
    public async Task DisabledWorkflowGateCannotProduceSuccessfulFenceReceipt()
    {
        var directory = TempRoot.Instance.NewDirectory("merge-fence-disabled");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        await using var issues = new IssueStore(path);
        await using var memory = new MemoryStore(path);
        var definition = WorkflowDefaults.Definition;
        definition = definition with { Steps = definition.Steps.Select(s => s with { Gates = s.Gates.Where(g => g != StageGates.Merge).ToArray() }).ToArray() };
        await memory.RememberAsync(WorkflowResolver.LiveKey, WorkflowResolver.Serialize(definition));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DeploymentMergeFence(memory).HoldAsync("unsafe-upgrade"));
        Assert.Empty(await memory.RecallAsync(DeploymentMergeFence.AuditKey));
    }
}
