using Forge.Agents;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// ProjectRepoBrief: the repo grounding injected into agent prompts
/// (2026-08-09: the talaria intake asked the operator what the tech
/// stack was — the clone already knows).
/// </summary>
public class ProjectRepoBriefTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repo-brief-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectRepoBriefTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void MissingRoot_DegradesToMarker()
    {
        Assert.Contains("unavailable", ProjectRepoBrief.Build(null));
        Assert.Contains("unavailable", ProjectRepoBrief.Build(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void DotnetRepo_DetectsStackTreeReadmeAndDocs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));      // skipped
        File.WriteAllText(Path.Combine(_root, "Talaria.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Talaria\n\nMessaging primitives for .NET agents.\n");
        File.WriteAllText(Path.Combine(_root, "docs", "transports.md"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "src", "Talaria.Core"));
        File.WriteAllText(Path.Combine(_root, "src", "Talaria.Core", "Talaria.Core.csproj"), "<Project />");

        var brief = ProjectRepoBrief.Build(_root);

        Assert.Contains(".NET / C#", brief);
        Assert.Contains("1 solution(s)", brief);
        Assert.Contains("1 project(s)", brief);
        Assert.Contains("src/", brief);
        Assert.Contains("docs/", brief);
        Assert.DoesNotContain("bin/", brief);
        Assert.Contains("# Talaria", brief);
        Assert.Contains("Messaging primitives for .NET agents.", brief);
        Assert.Contains("transports.md", brief);
    }

    [Fact]
    public void NodeRepo_DetectsNode()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        Assert.Contains("Node.js", ProjectRepoBrief.Build(_root));
    }

    [Fact]
    public void EmptyRepo_SaysUnknownNotSilence()
    {
        Assert.Contains("unknown", ProjectRepoBrief.Build(_root));
    }
}
