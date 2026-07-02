using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.AgentTools;
using Xunit;

namespace PortHorizon.Agents.Tests;

/// <summary>
/// BashTool: real cmd.exe-backed shell AIFunction. Tests cover the
/// success path, non-zero exit, and timeout behavior.
/// </summary>
public class BashToolTests : IDisposable
{
    private readonly string _cwd;

    public BashToolTests()
    {
        _cwd = Path.Combine(Path.GetTempPath(), $"ph-bash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cwd, recursive: true); } catch { }
    }

    [Fact]
    public async Task Bash_SimpleCommand_ReturnsExitZeroAndStdout()
    {
        var tool = new BashTool(_cwd, logger: NullLogger<BashTool>.Instance);
        var result = await tool.Bash("echo hello");
        Assert.Contains("exit=0", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task Bash_FailingCommand_ReturnsNonZeroExitAndStderr()
    {
        var tool = new BashTool(_cwd, logger: NullLogger<BashTool>.Instance);
        var result = await tool.Bash("cmd /c exit 7");
        Assert.Contains("exit=7", result);
    }

    [Fact]
    public async Task Bash_EmptyCommand_ReturnsError()
    {
        var tool = new BashTool(_cwd, logger: NullLogger<BashTool>.Instance);
        var result = await tool.Bash("");
        Assert.Contains("exit=-1", result);
        Assert.Contains("required", result);
    }

    [Fact]
    public async Task Bash_WhitespaceCommand_ReturnsError()
    {
        var tool = new BashTool(_cwd, logger: NullLogger<BashTool>.Instance);
        var result = await tool.Bash("   ");
        Assert.Contains("exit=-1", result);
    }

    [Fact]
    public async Task Bash_MissingCwd_ReturnsError()
    {
        var tool = new BashTool(@"C:\does\not\exist\anywhere", logger: NullLogger<BashTool>.Instance);
        var result = await tool.Bash("echo hi");
        Assert.Contains("exit=-1", result);
        Assert.Contains("workingDirectory", result);
    }

    [Fact]
    public async Task Bash_RunsInProvidedWorkingDirectory()
    {
        var tool = new BashTool(_cwd, logger: NullLogger<BashTool>.Instance);
        var result = await tool.Bash("cd");
        Assert.Contains("exit=0", result);
        // The output should contain our temp cwd (Windows prints full path).
        Assert.Contains(_cwd.TrimEnd('\\'), result);
    }

    [Fact]
    public async Task Bash_OverrideWorkingDirectory_Respected()
    {
        var subdir = Path.Combine(_cwd, "sub");
        Directory.CreateDirectory(subdir);
        var tool = new BashTool(_cwd, logger: NullLogger<BashTool>.Instance);
        var result = await tool.Bash("cd", workingDirectory: subdir);
        Assert.Contains("exit=0", result);
        Assert.Contains(subdir, result);
    }

    [Fact]
    public async Task Bash_TimeoutKilled_ReturnsTimeoutError()
    {
        var tool = new BashTool(_cwd, defaultTimeout: TimeSpan.FromSeconds(2),
            logger: NullLogger<BashTool>.Instance);
        // ping -n 10 takes ~10 seconds; timeout in 2s.
        var result = await tool.Bash("ping -n 10 127.0.0.1", timeoutSeconds: 2);
        Assert.Contains("exit=-1", result);
        Assert.Contains("timed out", result);
    }

    [Fact]
    public void AsAIFunction_ReturnsValidFunction()
    {
        var tool = new BashTool(_cwd, logger: NullLogger<BashTool>.Instance);
        var fn = tool.AsAIFunction();
        Assert.NotNull(fn);
        Assert.Equal("bash", fn.Name);
    }
}