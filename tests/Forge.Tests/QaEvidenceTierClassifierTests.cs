using Forge.Core;
using Forge.Reviewer;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// The deterministic 3-tier QA applicability gate: tier assignment,
/// highest-tier-wins on mixed diffs, conservative fallbacks (empty /
/// unclassifiable ⇒ code), the docs path set, and visual-prefix
/// resolution ($qa.visualPaths override → clientdev $territory).
/// </summary>
public sealed class QaEvidenceTierClassifierTests
{
    private static readonly string[] Visual = { "Client/" };

    [Fact]
    public void VisualPath_Tier1()
    {
        Assert.Equal(QaEvidenceTier.Visual,
            QaEvidenceTierClassifier.Classify(new[] { "Client/Scenes/Hud.tscn" }, Visual));
    }

    [Fact]
    public void CodePath_Tier2()
    {
        Assert.Equal(QaEvidenceTier.Code,
            QaEvidenceTierClassifier.Classify(new[] { "Core/Sim/World.cs" }, Visual));
    }

    [Fact]
    public void DocsOnlyDiff_Tier3()
    {
        Assert.Equal(QaEvidenceTier.Docs,
            QaEvidenceTierClassifier.Classify(
                new[] { ".gitignore", "docs/QA/policy.md", "test-results/qa/qa-1/evidence.png", "LICENSE", "README.md" },
                Visual));
    }

    [Fact]
    public void MixedDiff_HighestTierWins()
    {
        // docs + code ⇒ code
        Assert.Equal(QaEvidenceTier.Code,
            QaEvidenceTierClassifier.Classify(new[] { "docs/guide.md", "Core/World.cs" }, Visual));
        // docs + visual ⇒ visual
        Assert.Equal(QaEvidenceTier.Visual,
            QaEvidenceTierClassifier.Classify(new[] { "docs/guide.md", "Client/Hud.cs" }, Visual));
        // code + visual ⇒ visual
        Assert.Equal(QaEvidenceTier.Visual,
            QaEvidenceTierClassifier.Classify(new[] { "Core/World.cs", "Client/Hud.cs" }, Visual));
    }

    [Fact]
    public void EmptyDiff_ConservativeCode()
    {
        Assert.Equal(QaEvidenceTier.Code,
            QaEvidenceTierClassifier.Classify(Array.Empty<string>(), Visual));
    }

    [Fact]
    public void NoVisualPrefixesConfigured_NothingIsVisual()
    {
        // Fail-open toward LESS demand only when no visual surface is
        // configured at all: everything non-docs is tier 2.
        Assert.Equal(QaEvidenceTier.Code,
            QaEvidenceTierClassifier.Classify(new[] { "Client/Hud.cs" }, Array.Empty<string>()));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")] // deliberately NOT docs — conservative
    [InlineData("Core/Sim/World.cs")]
    [InlineData("scripts/qa.sh")]
    [InlineData("docs2/not-docs.txt")]       // prefix is "docs/", not "docs"
    public void NotDocsPaths(string path)
    {
        Assert.False(QaEvidenceTierClassifier.IsDocsPath(path));
    }

    [Theory]
    [InlineData("docs/QA/policy.md")]
    [InlineData("docs/arch.txt")]            // everything under docs/ is docs
    [InlineData("README.md")]                // **.md anywhere
    [InlineData("docs/QA/EVIDENCE.MD")]      // case-insensitive extension
    [InlineData(".gitignore")]
    [InlineData("sub/.gitattributes")]       // basename match anywhere
    [InlineData("LICENSE")]
    [InlineData("LICENSE.md")]
    [InlineData("test-results/qa/qa-1/01.png")]
    public void DocsPaths(string path)
    {
        Assert.True(QaEvidenceTierClassifier.IsDocsPath(path));
    }

    [Fact]
    public void VisualPathsOverride_WinsOverTerritory()
    {
        var territories = new Dictionary<string, RoleTerritory>
        {
            ["clientdev"] = new(new[] { "Client/" }, false),
        };
        var resolved = QaEvidenceTierClassifier.ResolveVisualPrefixes(
            territories, new[] { "Game/Rendering/" });
        Assert.Equal(new[] { "Game/Rendering/" }, resolved);
    }

    [Fact]
    public void VisualPathsOverride_EmptyMeansNothingVisual()
    {
        var territories = new Dictionary<string, RoleTerritory>
        {
            ["clientdev"] = new(new[] { "Client/" }, false),
        };
        var resolved = QaEvidenceTierClassifier.ResolveVisualPrefixes(
            territories, Array.Empty<string>());
        Assert.Empty(resolved);
    }

    [Fact]
    public void NoOverride_FallsBackToClientdevTerritory()
    {
        var territories = new Dictionary<string, RoleTerritory>
        {
            ["clientdev"] = new(new[] { "PortHorizon.Client/" }, false),
            ["coredev"] = new(new[] { "PortHorizon.Core/" }, false),
        };
        var resolved = QaEvidenceTierClassifier.ResolveVisualPrefixes(territories, null);
        Assert.Equal(new[] { "PortHorizon.Client/" }, resolved);
    }

    [Fact]
    public void NoOverride_NoTerritory_NothingVisual()
    {
        Assert.Empty(QaEvidenceTierClassifier.ResolveVisualPrefixes(null, null));
        Assert.Empty(QaEvidenceTierClassifier.ResolveVisualPrefixes(
            new Dictionary<string, RoleTerritory>(), null));
    }

    [Fact]
    public void MetadataValues()
    {
        Assert.Equal("visual", QaEvidenceTierClassifier.MetadataValue(QaEvidenceTier.Visual));
        Assert.Equal("code", QaEvidenceTierClassifier.MetadataValue(QaEvidenceTier.Code));
        Assert.Equal("docs", QaEvidenceTierClassifier.MetadataValue(QaEvidenceTier.Docs));
    }
}
