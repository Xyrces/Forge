using Forge.Agents;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Role prompt root resolution: a project's own agents/ dir wins;
/// otherwise the orchestrator's built-in defaults next to the app.
/// Without the fallback, non-Forge projects silently ran with
/// degraded role instructions.
/// </summary>
public class RolePromptRootTests : IDisposable
{
    private readonly string _workDir;

    public RolePromptRootTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-rpr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public void ProjectWithAgentsDir_Wins()
    {
        var projectRoot = Path.Combine(_workDir, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "agents"));
        var appBase = Path.Combine(_workDir, "app");

        var resolved = RolePromptRoot.Resolve(projectRoot, appBase);

        Assert.Equal(Path.Combine(projectRoot, "agents"), resolved);
    }

    [Fact]
    public void ProjectWithoutAgentsDir_FallsBackToBuiltIn()
    {
        var projectRoot = Path.Combine(_workDir, "project");
        Directory.CreateDirectory(projectRoot); // exists, but no agents/ inside
        var appBase = Path.Combine(_workDir, "app");

        var resolved = RolePromptRoot.Resolve(projectRoot, appBase);

        Assert.Equal(Path.Combine(appBase, "agents"), resolved);
    }
}
