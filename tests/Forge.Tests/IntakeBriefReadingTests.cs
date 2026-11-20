using Forge.Dashboard.Services;
using Xunit;

namespace Forge.Tests;

public sealed class IntakeBriefReadingTests
{
    [Fact]
    public void RevisedExcerptQuotesACompleteSentenceWithItsUnchangedContext()
    {
        var change = new IntakeBriefChange("Review", "A former rule.", "A current rule.", Edits:
        [
            new("Keep the existing evidence. The hold ", "unchanged"),
            new("can be cleared", "removed"),
            new("requires approval of the corrected head", "added"),
            new(" by the nominated human. Preserve the full audit.", "unchanged")
        ]);
        Assert.Equal(("The hold requires approval of the corrected head by the nominated human.", false),
            IntakeBriefReading.Excerpt(change));
    }

    [Fact]
    public void RemovalOnlyExcerptRetainsTheOldSentenceAndIdentifiesItsSource()
    {
        var change = new IntakeBriefChange("Scope", "Original.", "Current.", Edits:
        [
            new("Verify the source. ", "unchanged"),
            new("Manual approval is mandatory. ", "removed"),
            new("Retain the audit.", "unchanged")
        ]);
        Assert.Equal(("Manual approval is mandatory.", true), IntakeBriefReading.Excerpt(change));
    }

    [Fact]
    public void ExcerptKeepsTheFirstSubstantiveChangeAheadOfALongerTrailingCaveat()
    {
        var change = new IntakeBriefChange("Review", "Before.", "After.", Edits:
        [
            new("Use the normal workflow. ", "unchanged"),
            new("The hold requires the nominated human to approve the corrected head. ", "added"),
            new("Retain all evidence. ", "unchanged"),
            new("Releasing an optional hold is a separate operator action and never waives project policy or an outstanding human objection.", "added")
        ]);
        Assert.Equal(("The hold requires the nominated human to approve the corrected head.", false),
            IntakeBriefReading.Excerpt(change));
    }

    [Fact]
    public void HistoricalDraftingNoteIsFoldedOnlyWhenItIsTheExactStandaloneEnding()
    {
        var note = IntakeBriefReading.DraftingNote;
        Assert.Equal("Scope.", IntakeBriefReading.BodyWithoutDraftingNote("Scope.\n\n" + note + "\n"));
        Assert.Equal("Discuss " + note, IntakeBriefReading.BodyWithoutDraftingNote("Discuss " + note));
        Assert.Equal(note + "\nMore scope.", IntakeBriefReading.BodyWithoutDraftingNote(note + "\nMore scope."));
        Assert.True(IntakeBriefReading.IsDraftingNote(new("Note", null, note)));
        Assert.False(IntakeBriefReading.IsDraftingNote(new("Scope", null, note + "\nMore scope.")));
    }
}
