using Forge.AgentTools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Forge.Tests;

public sealed class BashCancellationTests
{
    [Fact]
    public async Task FunctionBinderPropagatesCancellationAndDoesNotExposeTokenInSchema()
    {
        var tool = new BashTool(Environment.CurrentDirectory).AsAIFunction();
        Assert.DoesNotContain("cancellationToken", tool.JsonSchema.ToString());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["command"] = command }, cancellation.Token));
    }

    [Fact]
    public async Task OutputIsBoundedWhilePipeIsFullyDrained()
    {
        var command = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,10000) do @echo 01234567890123456789"
            : "yes 01234567890123456789 | head -n 10000";
        var result = await new BashTool(Environment.CurrentDirectory).Bash(command);
        Assert.StartsWith("exit=0", result);
        Assert.Contains("[output truncated", result);
        Assert.True(result.Length < 66000);
    }
}
