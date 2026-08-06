using Forge.AgentTools;
using Xunit;

namespace Forge.Tests;

public class PushHygieneTests : IDisposable
{
    private readonly string _workDir;

    public PushHygieneTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-hyg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("PortHorizon.Core/UmbilicalConnectorSystem.cs.bak")]
    [InlineData("src/Foo.cs.orig")]
    [InlineData("src/Foo.cs.rej")]
    [InlineData("notes.tmp")]
    [InlineData("src/Foo.cs~")]
    [InlineData(".swp")]
    public void JunkArtifacts_AreViolations(string path)
    {
        var violations = PushHygiene.Check(_workDir, new[] { path });
        Assert.Single(violations);
        Assert.Contains(path, violations[0]);
        Assert.Contains("junk artifact", violations[0]);
    }

    [Fact]
    public void CleanFiles_Pass()
    {
        File.WriteAllText(Path.Combine(_workDir, "Foo.cs"), "class Foo {}");
        var violations = PushHygiene.Check(_workDir, new[] { "Foo.cs", "docs/vision.md", "assets/ship.json" });
        Assert.Empty(violations);
    }

    [Fact]
    public void OversizedNewFile_IsViolation()
    {
        var big = Path.Combine(_workDir, "big.bin");
        File.WriteAllBytes(big, new byte[PushHygiene.MaxNewFileBytes + 1]);
        var violations = PushHygiene.Check(_workDir, new[] { "big.bin" });
        Assert.Single(violations);
        Assert.Contains("big.bin", violations[0]);
        Assert.Contains("operator sign-off", violations[0]);
    }

    [Fact]
    public void JunkCheckWins_OverSizeProbe()
    {
        // A .bak that is ALSO huge reports as junk (one finding).
        var path = Path.Combine(_workDir, "huge.bak");
        File.WriteAllBytes(path, new byte[PushHygiene.MaxNewFileBytes + 1]);
        var violations = PushHygiene.Check(_workDir, new[] { "huge.bak" });
        Assert.Single(violations);
        Assert.Contains("junk artifact", violations[0]);
    }
}
