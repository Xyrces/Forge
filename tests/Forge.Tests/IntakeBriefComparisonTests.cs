using Forge.Dashboard.Services;
using Xunit;

namespace Forge.Tests;

public sealed class IntakeBriefComparisonTests
{
    [Fact]
    public void ChangedAcceptanceIsSeparatedFromUnchangedScopeAndCodeHeadings()
    {
        const string before = "## Goal\nShow clear progress.\n\n## Acceptance\nOpen the page.\n\n## Example\n```md\n## Code heading\n```\n";
        var after = before.Replace("Open the page.", "Open the page and inspect the status.");
        var changes = IntakeBriefComparison.Compare(before, after);
        Assert.Equal(3, changes.Count);
        var changed = Assert.Single(changes, change => change.Changed);
        Assert.Equal("Acceptance", changed.Heading);
        Assert.Contains("Open the page.", changed.Before);
        Assert.Contains("inspect the status", changed.After);
    }

    [Fact]
    public void AddedAndRemovedSectionsRemainVisibleAndRepeatedHeadingsStayDistinct()
    {
        var changes = IntakeBriefComparison.Compare("## Scope\nA\n## Scope\nB\n## Later\nC\n",
            "## Scope\nA\n## Scope\nRevised B\n## New\nD\n");
        Assert.Equal(4, changes.Count);
        Assert.Single(changes, change => change.Heading == "Scope" && !change.Changed);
        Assert.Single(changes, change => change.Heading == "Scope (2)" && change.Changed);
        Assert.Single(changes, change => change.Heading == "Later" && change.After is null);
        Assert.Single(changes, change => change.Heading == "New" && change.Before is null);
    }

    [Fact]
    public void ReorderedSectionsAndPreambleHeadingNamesRemainDistinguishable()
    {
        var changes = IntakeBriefComparison.Compare("Preface\n\n## Introduction\nA\n\n## Goal\nB\n\n",
            "Preface\n\n## Goal\nB\n\n## Introduction\nA\n\n");
        Assert.Equal(3, changes.Count);
        Assert.False(changes[0].Changed);
        Assert.Equal(2, changes.Count(change => change.Moved));
    }

    [Fact]
    public void LiteralHeadingTextCannotAliasDuplicateOrNestedHeadings()
    {
        const string before = "## Scope\nfirst\n## Scope\nsecond\n## Scope (2)\nliteral\n## Scope / Goal\nflat\n## Scope\nthird\n### Goal\nnested\n";
        var changes = IntakeBriefComparison.Compare(before, before.Replace("literal", "new literal").Replace("nested", "new nested"));
        var edited = changes.Where(change => change.Changed).ToList();
        Assert.Equal(2, edited.Count);
        Assert.Contains(edited, change => change.Before!.Contains("literal") && change.After!.Contains("new literal"));
        Assert.Contains(edited, change => change.Before!.Contains("nested") && change.After!.Contains("new nested"));
    }

    [Fact]
    public void UnstructuredBriefStillHasACompleteComparison()
    {
        var change = Assert.Single(IntakeBriefComparison.Compare("Original.", "Revised."));
        Assert.Equal("Original.", change.Before);
        Assert.Equal("Revised.", change.After);
    }
}
