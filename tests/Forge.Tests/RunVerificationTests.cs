using Forge.Core;
using Forge.AgentTools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class RunVerificationTests : IDisposable
{
    private readonly string _workDir;

    public RunVerificationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task PassingCommand_Ok()
    {
        var result = await RunVerification.RunAsync(
            _workDir, new[] { "echo hello", "true" },
            NullLogger.Instance, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task FailingCommand_ReportsExitAndOutputTail()
    {
        var result = await RunVerification.RunAsync(
            _workDir, new[] { "echo BOOM-MARKER; exit 3" },
            NullLogger.Instance, CancellationToken.None);
        Assert.False(result.Ok);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("exited 3", failure);
        Assert.Contains("BOOM-MARKER", failure);
    }

    [Fact]
    public async Task Timeout_ReportsTimedOut()
    {
        var result = await RunVerification.RunAsync(
            _workDir, new[] { "sleep 30" },
            NullLogger.Instance, CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(500));
        Assert.False(result.Ok);
        Assert.Contains("timed out", Assert.Single(result.Failures));
    }

    [Fact]
    public async Task MultipleCommands_CollectsAllFailures()
    {
        var result = await RunVerification.RunAsync(
            _workDir, new[] { "false", "true", "exit 2" },
            NullLogger.Instance, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(2, result.Failures.Count);
    }
}
