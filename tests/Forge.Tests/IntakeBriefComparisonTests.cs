using Forge.Dashboard.Services;
using Xunit;

namespace Forge.Tests;

public sealed class IntakeBriefComparisonTests
{
    [Fact]
    public void MixedLinkAndWordingEditsKeepTheOriginalDestinations()
    {
        var change = Assert.Single(IntakeBriefComparison.Compare("See [policy](old-url) today.", "See [policy](new-url) tomorrow."));
        Assert.True(change.HasMarkup);
        Assert.NotEmpty(change.InlineEdits);
        Assert.Contains("old-url", change.Before);
        Assert.Contains("new-url", change.After);
    }

    [Theory]
    [InlineData("1. First\n2. Second", "5. First\n6. Second")]
    [InlineData("## Goal\nDeliver.", "### Goal\nDeliver.")]
    public void StructureOnlyChangesKeepBothVersionsReviewable(string before, string after)
    {
        var change = Assert.Single(IntakeBriefComparison.Compare(before, after));
        Assert.True(change.Changed);
        Assert.Equal(before, change.Before);
        Assert.Equal(after, change.After);
    }

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
    public void LongAcceptanceSectionShowsOnlyChangedCriteriaAndAddedParagraph()
    {
        const string before = "## Acceptance criteria\n1. **Serving revision:** Identify the release.\n2. **Browser check:** Observe every lane.\n3. **Protected PR:** Remove the human hold, then merge.\n4. **Evidence:** Record hold removal.\n5. **History:** Inspect previous work.\n6. **Repeatability:** Provide steps.\n";
        var after = before.Replace("Remove the human hold, then merge.", "Approve the corrected head with the human hold still present.")
            .Replace("Record hold removal.", "Record approval and retain the hold label.")
            + "\nDraft only; explicit approval is required.\n";
        var changes = IntakeBriefComparison.Compare(before, after);
        Assert.Equal(7, changes.Count);
        Assert.Equal(3, changes.Count(change => change.Changed));
        Assert.All(changes, change => Assert.Equal("Acceptance criteria", change.Section));
        var criterion = Assert.Single(changes, change => change.Heading == "Protected PR");
        Assert.Equal(3, criterion.Ordinal);
        Assert.Equal("Remove the human hold, then merge.", criterion.Before);
        Assert.Equal("Approve the corrected head with the human hold still present.", criterion.After);
        Assert.DoesNotContain("Acceptance criteria", criterion.After);
        Assert.DoesNotContain("Protected PR", criterion.After);
        Assert.NotEmpty(criterion.InlineEdits);
        Assert.Equal(criterion.Before, string.Concat(criterion.InlineEdits.Where(edit => edit.Kind != "added").Select(edit => edit.Text)));
        Assert.Equal(criterion.After, string.Concat(criterion.InlineEdits.Where(edit => edit.Kind != "removed").Select(edit => edit.Text)));
    }

    [Fact]
    public void OrderedCriteriaPreserveRenderedOrdinalAndStartNumber()
    {
        var changes = IntakeBriefComparison.Compare("## Scope\n7. **First:** A\n1. **Second:** B\n1. **Third:** C\n",
            "## Scope\n7. **First:** A\n1. **Second:** Updated B\n1. **Third:** C\n");
        Assert.Equal(new int?[] { 7, 8, 9 }, changes.Select(change => change.Ordinal));
        Assert.Equal(8, Assert.Single(changes, change => change.Changed).Ordinal);
        var bullets = IntakeBriefComparison.Compare("- A\n- B\n", "- A\n- Updated B\n");
        Assert.All(bullets, change => Assert.Null(change.Ordinal));
    }

    [Fact]
    public void InsertingAnUnlabelledCriterionDoesNotMarkLaterCriteriaAsRewritten()
    {
        var changes = IntakeBriefComparison.Compare("## Scope\n- First task.\n- Last task.\n",
            "## Scope\n- First task.\n- New task.\n- Last task.\n");
        var added = Assert.Single(changes, change => change.Changed);
        Assert.Null(added.Before);
        Assert.Equal("New task.", added.After);
    }

    [Fact]
    public void DuplicateParagraphsRemainSeparateAndReorderedCriteriaAreReported()
    {
        var duplicate = IntakeBriefComparison.Compare("Same.\n\nSame.\n", "Same.\n\nSame.\n");
        Assert.Equal(2, duplicate.Count);
        Assert.All(duplicate, change => Assert.False(change.Changed));
        var moved = IntakeBriefComparison.Compare("## Scope\n- A\n- B\n", "## Scope\n- B\n- A\n");
        Assert.Equal(2, moved.Count);
        Assert.All(moved, change => Assert.True(change.Moved));
    }

    [Fact]
    public void CodeAndFormattingOnlyChangesKeepFullMarkdownAndLargeInputsRemainBounded()
    {
        var code = Assert.Single(IntakeBriefComparison.Compare("```cs\nold();\n```", "```cs\nnew();\n```"));
        Assert.Empty(code.InlineEdits);
        Assert.Contains("```cs", code.After);
        var formatting = Assert.Single(IntakeBriefComparison.Compare("**Safe**", "*Safe*"));
        Assert.True(formatting.Changed);
        Assert.Empty(formatting.InlineEdits);
        var many = string.Join("\n\n", Enumerable.Range(0, 514).Select(i => "Paragraph " + i));
        var fallback = Assert.Single(IntakeBriefComparison.Compare(many, many + " Changed."));
        Assert.Equal(many, fallback.Before);
        Assert.Equal(many + " Changed.", fallback.After);
    }

    [Fact]
    public void UnstructuredBriefStillHasACompleteComparison()
    {
        var change = Assert.Single(IntakeBriefComparison.Compare("Original.", "Revised."));
        Assert.Equal("Original.", change.Before);
        Assert.Equal("Revised.", change.After);
    }
}
