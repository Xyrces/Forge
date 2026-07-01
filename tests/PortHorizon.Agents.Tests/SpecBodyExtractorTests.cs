using PortHorizon.Agents.Specs;
using Xunit;

namespace PortHorizon.Agents.Tests;

public class SpecBodyExtractorTests
{
    private readonly SpecBodyExtractor _extractor = new();

    [Fact]
    public void Extract_NullOrEmptyBody_ReturnsEmpty()
    {
        var a = _extractor.Extract(null);
        var b = _extractor.Extract("");
        Assert.Empty(a.Diagrams);
        Assert.Empty(a.Touches);
        Assert.Empty(a.Deps);
    }

    [Fact]
    public void Extract_RecognizesMermaidFence_AndDetectsKind()
    {
        var body = """
            # Title

            ## Diagrams
            ```mermaid
            sequenceDiagram
                A->>B: hi
            ```
            """;

        var got = _extractor.Extract(body);

        Assert.Single(got.Diagrams);
        Assert.Equal("sequencediagram", got.Diagrams[0].Kind);
        Assert.Equal(0, got.Diagrams[0].Ordinal);
        Assert.Equal("Diagrams", got.Diagrams[0].Title);
        Assert.Contains("A->>B: hi", got.Diagrams[0].Source);
    }

    [Fact]
    public void Extract_DetectsFlowchartKind()
    {
        var body = """
            ```mermaid
            flowchart LR
              A --> B
            ```
            """;

        var got = _extractor.Extract(body);

        Assert.Single(got.Diagrams);
        Assert.Equal("flowchart", got.Diagrams[0].Kind);
    }

    [Fact]
    public void Extract_DiagramsAreNumberedInOrder()
    {
        var body = """
            ## Diagrams
            ```mermaid
            flowchart LR
              A --> B
            ```
            ```mermaid
            sequenceDiagram
              A->>B: 1
            ```
            ```mermaid
            classDiagram
              class A
            ```
            """;

        var got = _extractor.Extract(body);

        Assert.Equal(3, got.Diagrams.Count);
        Assert.Equal(0, got.Diagrams[0].Ordinal);
        Assert.Equal(1, got.Diagrams[1].Ordinal);
        Assert.Equal(2, got.Diagrams[2].Ordinal);
        Assert.Equal("flowchart", got.Diagrams[0].Kind);
        Assert.Equal("sequencediagram", got.Diagrams[1].Kind);
        Assert.Equal("classdiagram", got.Diagrams[2].Kind);
    }

    [Fact]
    public void Extract_NonMermaidFences_AreIgnored()
    {
        var body = """
            ## Diagrams
            ```csharp
            var x = 1;
            ```
            ```mermaid
            flowchart LR
              A --> B
            ```
            """;

        var got = _extractor.Extract(body);

        Assert.Single(got.Diagrams);
        Assert.Equal("flowchart", got.Diagrams[0].Kind);
    }

    [Fact]
    public void Extract_Touches_BulletListParsed()
    {
        var body = """
            ## Touches
            - PortHorizon.Core.Auth
            - PortHorizon.Dashboard.Theming
            """;

        var got = _extractor.Extract(body);

        Assert.Equal(2, got.Touches.Count);
        Assert.Equal("PortHorizon.Core.Auth", got.Touches[0].ModuleId);
        Assert.Equal("PortHorizon.Dashboard.Theming", got.Touches[1].ModuleId);
    }

    [Fact]
    public void Extract_Touches_SubBulletsAttachAsRationale()
    {
        var body = """
            ## Touches
            - PortHorizon.Core.Auth
                - new login flow uses claims middleware
            - PortHorizon.Dashboard.Theming
            """;

        var got = _extractor.Extract(body);

        Assert.Equal(2, got.Touches.Count);
        Assert.Equal("PortHorizon.Core.Auth", got.Touches[0].ModuleId);
        Assert.Contains("claims middleware", got.Touches[0].Rationale);
        Assert.Null(got.Touches[1].Rationale);
    }

    [Fact]
    public void Extract_Deps_ParsesBlocksDependsOnRelated()
    {
        var body = """
            ## Dependencies
            - blocks spec-portal-redirect
            - depends_on spec-auth-claims
            - related spec-theming-base — shared token surface
            """;

        var got = _extractor.Extract(body);

        Assert.Equal(3, got.Deps.Count);
        Assert.Equal("blocks", got.Deps[0].Kind);
        Assert.Equal("spec-portal-redirect", got.Deps[0].TargetSpecId);
        Assert.Null(got.Deps[0].Rationale);
        Assert.Equal("depends_on", got.Deps[1].Kind);
        Assert.Equal("spec-auth-claims", got.Deps[1].TargetSpecId);
        Assert.Equal("related", got.Deps[2].Kind);
        Assert.Equal("spec-theming-base", got.Deps[2].TargetSpecId);
        Assert.Contains("shared token surface", got.Deps[2].Rationale);
    }

    [Fact]
    public void Extract_Deps_UnknownKindSkipped()
    {
        var body = """
            ## Dependencies
            - blocks spec-a
            - wibblywobbly spec-b
            """;

        var got = _extractor.Extract(body);

        Assert.Single(got.Deps);
        Assert.Equal("spec-a", got.Deps[0].TargetSpecId);
    }

    [Fact]
    public void Extract_OrphanMermaidBlocksOutsideDiagramsSection_ArePickedUp()
    {
        // The agent's prose might include a diagram before any
        // ## headings. We pick those up too.
        var body = """
            # Heading

            ```mermaid
            flowchart LR
              A --> B
            ```

            ## Summary
            This is the summary.
            """;

        var got = _extractor.Extract(body);

        Assert.Single(got.Diagrams);
        Assert.Equal("flowchart", got.Diagrams[0].Kind);
    }

    [Fact]
    public void Extract_SectionSplit_ReturnsAllSections()
    {
        var body = """
            ## Summary
            One paragraph.

            ## Acceptance criteria
            - one
            - two

            ## Out of scope
            nope
            """;

        var sections = SpecBodyExtractor.SplitSections(body);
        Assert.Equal(3, sections.Count);
        Assert.Equal("Summary", sections[0].Title);
        Assert.Equal("Acceptance criteria", sections[1].Title);
        Assert.Equal("Out of scope", sections[2].Title);
    }

    [Fact]
    public void Extract_PreambleBeforeFirstHeading_IsASection()
    {
        var body = """
            Some prose before any heading.

            ## Summary
            The summary.
            """;

        var sections = SpecBodyExtractor.SplitSections(body);
        Assert.Equal(2, sections.Count);
        Assert.Equal("", sections[0].Title);
        Assert.Contains("Some prose", sections[0].Content);
        Assert.Equal("Summary", sections[1].Title);
    }
}