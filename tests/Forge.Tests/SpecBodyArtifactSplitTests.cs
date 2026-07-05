using Forge.Specs;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// P5.3 — spec body split: header (always inlined into prompts) +
/// per-artifact bodies (read on demand via the read_artifact tool).
/// The post-processor extracts every
/// `&lt;!-- artifact:kind:title --&gt;` block into a separate
/// design_artifact row and replaces the marker in the header with
/// a `[read_artifact design-{id}]` placeholder.
/// </summary>
public class SpecBodyArtifactSplitTests
{
    private readonly SpecBodyExtractor _extractor = new();

    [Fact]
    public void EmptyBody_ReturnsEmpty()
    {
        var got = _extractor.ExtractForReadArtifact("spec-task-1", 1, "");
        Assert.Equal("", got.Header);
        Assert.Empty(got.NewArtifacts);
    }

    [Fact]
    public void NullBody_ReturnsEmpty()
    {
        var got = _extractor.ExtractForReadArtifact("spec-task-1", 1, null);
        Assert.Equal("", got.Header);
        Assert.Empty(got.NewArtifacts);
    }

    [Fact]
    public void NoMarkers_HeaderIsBodyAsIs()
    {
        var body = "## Summary\nPlain markdown body.";
        var got = _extractor.ExtractForReadArtifact("spec-1", 1, body);
        Assert.Equal(body, got.Header);
        Assert.Empty(got.NewArtifacts);
    }

    [Fact]
    public void SingleMarker_ExtractsBlockAsArtifact()
    {
        var body = """
            ## Summary
            Brief summary.
            <!-- artifact:wireframe:Login screen -->
            <svg>...</svg>
            More text after.
            """;
        var got = _extractor.ExtractForReadArtifact("spec-task-1", 1, body);
        Assert.Single(got.NewArtifacts);
        var art = got.NewArtifacts[0];
        Assert.Equal("wireframe", art.Kind);
        Assert.Equal("Login screen", art.Title);
        // The artifact body is everything between the marker and
        // end-of-document (no second marker in this test).
        Assert.Contains("<svg>", art.Body);
        // "More text after." is part of the artifact body, not
        // the header — it's only visible via read_artifact.
        Assert.Contains("More text after.", art.Body);
        // The header keeps the prefix (everything before the
        // marker) + the placeholder.
        Assert.Contains("Brief summary.", got.Header);
        Assert.DoesNotContain("<!-- artifact", got.Header);
        Assert.Contains("[read_artifact design-spec-task-1-1-1]", got.Header);
    }

    [Fact]
    public void MultipleMarkers_EachExtracted()
    {
        var body = """
            ## Summary
            Summary text.
            <!-- artifact:wireframe:Login -->
            A
            <!-- artifact:mockup:Settings -->
            B
            End.
            """;
        var got = _extractor.ExtractForReadArtifact("spec-x", 1, body);
        Assert.Equal(2, got.NewArtifacts.Count);
        Assert.Equal("wireframe", got.NewArtifacts[0].Kind);
        Assert.Equal("Login", got.NewArtifacts[0].Title);
        Assert.Equal("A", got.NewArtifacts[0].Body.Trim());
        Assert.Equal("mockup", got.NewArtifacts[1].Kind);
        Assert.Equal("Settings", got.NewArtifacts[1].Title);
        // The second artifact body extends to end-of-document
        // (no third marker), so it includes "B" + "End.".
        Assert.Contains("B", got.NewArtifacts[1].Body);
        Assert.Contains("End.", got.NewArtifacts[1].Body);
        // The header has both placeholders + the prefix before
        // the first marker.
        Assert.Contains("[read_artifact design-spec-x-1-1]", got.Header);
        Assert.Contains("[read_artifact design-spec-x-1-2]", got.Header);
        Assert.Contains("Summary text.", got.Header);
    }

    [Fact]
    public void Idempotent_RerunningOnPostProcessedBody_NoNewArtifacts()
    {
        var body = """
            ## Summary
            Brief.
            <!-- artifact:wireframe:Login -->
            <svg>...</svg>
            """;
        var first = _extractor.ExtractForReadArtifact("spec-1", 1, body);
        Assert.Single(first.NewArtifacts);
        // Re-run on the slim header. The marker is gone -> no new
        // artifacts.
        var second = _extractor.ExtractForReadArtifact("spec-1", 2, first.Header);
        Assert.Empty(second.NewArtifacts);
        Assert.Equal(first.Header, second.Header);
    }

    [Fact]
    public void EmptyMarker_StillAddsPlaceholder()
    {
        // Marker with empty body (just whitespace) -> placeholder
        // is still inserted (so the LLM knows the marker was
        // there), but no artifact is added. The placeholder is
        // "[read_artifact empty-{i}]" rather than a design-*
        // id; this signals "the Designer emitted a marker but
        // didn't fill in a body" so the DesignHygieneChecker
        // (P5.4) can flag it.
        var body = """
            <!-- artifact:wireframe:Empty -->
            """;
        var got = _extractor.ExtractForReadArtifact("spec-1", 1, body);
        Assert.Empty(got.NewArtifacts);
        Assert.Contains("[read_artifact empty-1]", got.Header);
    }

    [Fact]
    public void UnknownKind_StillExtractsButMapsToComponentSpec()
    {
        var body = """
            <!-- artifact:weird-kind:Title -->
            Content
            """;
        var got = _extractor.ExtractForReadArtifact("spec-1", 1, body);
        Assert.Single(got.NewArtifacts);
        // Unknown kinds map to ComponentSpec (safe default). The
        // DesignHygieneChecker (P5.4) reports the unknown kind.
        Assert.Equal("component-spec", got.NewArtifacts[0].Kind);
    }

    [Fact]
    public void DeterministicIds_SameInputsProduceSameId()
    {
        var body = """
            <!-- artifact:wireframe:Login -->
            A
            """;
        var first = _extractor.ExtractForReadArtifact("spec-task-1", 2, body);
        var second = _extractor.ExtractForReadArtifact("spec-task-1", 2, body);
        // The post-processor always produces the same id for the
        // same specId+version+index. Idempotency + auditability.
        var firstId = ExtractPlaceholderId(first.Header);
        var secondId = ExtractPlaceholderId(second.Header);
        Assert.Equal(firstId, secondId);
        Assert.Equal("design-spec-task-1-2-1", firstId);
    }

    private static string ExtractPlaceholderId(string header)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            header, @"\[read_artifact\s+([^\]]+)\]");
        return m.Success ? m.Groups[1].Value : "<none>";
    }
}
