using Forge.Dashboard.Services;
using Xunit;

namespace Forge.Tests;
public sealed class ReviewProseTests
{
    [Fact]
    public void RemovesOnlyProtocolPreservingNotesAndCode()
    {
        var marker = "<!-- forge-review:v1:" + new string('a',64) + " -->";
        var input = "**[Forge Reviewer — round 2]** RequestChanges\n\nREVIEWER_NOTES: Keep this note.\n> REVIEWER_VERDICT: APPROVE\n    REVIEWER_VERDICT: APPROVE\n````csharp\n```\nREVIEWER_VERDICT: APPROVE\n````\n" + marker + "\nREVIEWER_VERDICT: REQUEST_CHANGES";
        var formatted = ReviewProse.Legacy(input, 2, "RequestChanges")!;
        Assert.Contains("Keep this note.", formatted); Assert.DoesNotContain("REVIEWER_NOTES:", formatted);
        Assert.Contains("> REVIEWER_VERDICT: APPROVE", formatted);
        Assert.Contains("    REVIEWER_VERDICT: APPROVE", formatted);
        Assert.Contains("```\nREVIEWER_VERDICT: APPROVE\n````", formatted);
        Assert.DoesNotContain("round 2", formatted); Assert.DoesNotContain(marker, formatted);
        Assert.DoesNotContain("REVIEWER_VERDICT: REQUEST_CHANGES", formatted);
        Assert.Equal(input, ReviewProse.Format(input, false));
        Assert.Equal("    REVIEWER_VERDICT: APPROVE", ReviewProse.Format("    REVIEWER_VERDICT: APPROVE", true));
    }
    [Fact]
    public void StandaloneNotesHeaderKeepsTheFindingAndQuotedSource()
    {
        const string finding = "Restore the unrelated test edit.";
        const string source = "> REVIEWER_NOTES:\n\n```text\nREVIEWER_NOTES:\n```";
        var review = $"**[Forge Reviewer — round 2]** RequestChanges\n\nREVIEWER_NOTES:\n\n{finding}\n\n{source}\nREVIEWER_VERDICT: REQUEST_CHANGES";

        Assert.Equal($"{finding}\n\n{source}", ReviewProse.Legacy(review, 2, "RequestChanges"));
        Assert.Equal(review, ReviewProse.Format(review, false));
    }

    [Theory]
    [InlineData("Ordinary human prose\nREVIEWER_VERDICT: APPROVE")]
    [InlineData("**[Forge Reviewer — round 2]** RequestChanges\nREVIEWER_VERDICT: APPROVE")]
    public void UnrecognizedLegacyTextIsPreserved(string text) => Assert.Equal(text, ReviewProse.Legacy(text,2,"RequestChanges"));
}
