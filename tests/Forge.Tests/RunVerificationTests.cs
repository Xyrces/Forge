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
        _workDir = TempRoot.Instance.NewDirectory("verify");
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

    [Fact]
    public void ExtractFailedTests_ParsesXunitFailLines()
    {
        // Real output shape from the porthorizon task-601 failure.
        var output = """
            [xUnit.net 00:00:02.29]     PortHorizon.Tests.Systems.MaterialReservationSystemTests.Concurrent_MultipleItemArchetypes_ThreadSafeTotals [FAIL]
            [xUnit.net 00:00:02.31]     PortHorizon.Tests.Systems.MaterialReservationSystemTests.Concurrent_ReserveMultipleJobs_ThreadSafeTotals [FAIL]
            Failed!  - Failed:     2, Passed:  2567, Skipped:     0, Total:  2569, Duration: 11 s
            """;
        var names = RunVerification.ExtractFailedTests(output);
        Assert.Equal(2, names.Count);
        Assert.Contains("PortHorizon.Tests.Systems.MaterialReservationSystemTests.Concurrent_MultipleItemArchetypes_ThreadSafeTotals", names);
        Assert.Contains("PortHorizon.Tests.Systems.MaterialReservationSystemTests.Concurrent_ReserveMultipleJobs_ThreadSafeTotals", names);
    }

    [Fact]
    public void ExtractFailedTests_ParsesVsTestSummaryLines()
    {
        var output = """
              Failed Forge.Tests.Core.IssueStoreTests.Claim_Assigns [12 ms]
              Passed Forge.Tests.Core.IssueStoreTests.Create_Works [3 ms]
            """;
        var names = RunVerification.ExtractFailedTests(output);
        Assert.Equal(new[] { "Forge.Tests.Core.IssueStoreTests.Claim_Assigns" }, names);
    }

    [Fact]
    public void ExtractFailedTests_IgnoresCleanOutput()
    {
        Assert.Empty(RunVerification.ExtractFailedTests(
            "Passed!  - Failed:     0, Passed:  1268, Skipped:     2, Total:  1270"));
    }

    [Fact]
    public async Task FlakyTests_PassInIsolation_GatePassesWithNote()
    {
        // Fake dotnet: full-run "test" fails with xUnit-style [FAIL]
        // lines; the --filter isolation re-run passes.
        var shimDir = Path.Combine(_workDir, "shim");
        Directory.CreateDirectory(shimDir);
        var shim = Path.Combine(shimDir, "dotnet");
        await File.WriteAllTextAsync(shim, """
            #!/bin/bash
            if [[ "$*" == *--filter* ]]; then
              echo "Passed! - Failed: 0, Passed: 2"
              exit 0
            fi
            echo "[xUnit.net 00:00:01.00]     Some.Tests.FlakyTest.One [FAIL]"
            echo "[xUnit.net 00:00:01.01]     Some.Tests.FlakyTest.Two [FAIL]"
            echo "Failed! - Failed: 2, Passed: 100"
            exit 1
            """);
        File.SetUnixFileMode(shim, UnixFileMode.UserExecute | UnixFileMode.UserRead);

        var result = await RunVerification.RunAsync(
            _workDir, new[] { $"export PATH=\"{shimDir}:$PATH\"; dotnet test -c Release --nologo" },
            NullLogger.Instance, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Empty(result.Failures);
        var note = Assert.Single(result.FlakyPasses!);
        Assert.Contains("Some.Tests.FlakyTest.One", note);
        Assert.Contains("quarantined as flaky", note);
    }

    [Fact]
    public async Task RealFailures_StillFailInIsolation_GateFails()
    {
        var shimDir = Path.Combine(_workDir, "shim");
        Directory.CreateDirectory(shimDir);
        var shim = Path.Combine(shimDir, "dotnet");
        await File.WriteAllTextAsync(shim, """
            #!/bin/bash
            if [[ "$*" == *--filter* ]]; then
              echo "Failed! - Failed: 1"
              exit 1
            fi
            echo "[xUnit.net 00:00:01.00]     Some.Tests.Real.Broken [FAIL]"
            exit 1
            """);
        File.SetUnixFileMode(shim, UnixFileMode.UserExecute | UnixFileMode.UserRead);

        var result = await RunVerification.RunAsync(
            _workDir, new[] { $"export PATH=\"{shimDir}:$PATH\"; dotnet test -c Release --nologo" },
            NullLogger.Instance, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Single(result.Failures);
        Assert.Null(result.FlakyPasses);
    }

    [Fact]
    public async Task NonTestCommandFailure_NoRetry_GateFails()
    {
        var result = await RunVerification.RunAsync(
            _workDir, new[] { "echo '[xUnit.net 00:00:01.00]     Fake.Test [FAIL]'; exit 1" },
            NullLogger.Instance, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Null(result.FlakyPasses);
    }
}
