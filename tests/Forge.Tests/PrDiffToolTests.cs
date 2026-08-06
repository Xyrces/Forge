using Forge.AgentTools;
using Forge.Reviewer;
using Xunit;

namespace Forge.Tests;

public class PrDiffToolTests
{
    private const string Diff =
        "diff --git a/A.cs b/A.cs\n+aaa\n" +
        "diff --git a/B.cs b/B.cs\n+bbb\n+bbb2\n" +
        "diff --git a/C.cs b/C.cs\n+ccc\n";

    [Fact]
    public void Manifest_ListsFilesWithSizes()
    {
        var tool = new PrDiffTool(Diff);
        var manifest = tool.Page(file: null, offset: 0, limit: PrDiffTool.DefaultWindowChars);
        Assert.Contains("3 file(s)", manifest);
        Assert.Contains("A.cs", manifest);
        Assert.Contains("B.cs", manifest);
        Assert.Contains("C.cs", manifest);
        Assert.Contains("chars total", manifest);
    }

    [Fact]
    public void FileScope_ReturnsJustThatFile()
    {
        var tool = new PrDiffTool(Diff);
        var page = tool.Page("B.cs", 0, PrDiffTool.DefaultWindowChars);
        Assert.Contains("+bbb2", page);
        Assert.DoesNotContain("+aaa", page);
        Assert.Contains("end of file B.cs", page);
    }

    [Fact]
    public void WholeDiffWindow_Paginates()
    {
        var big = string.Concat(Enumerable.Repeat(Diff, 8)); // ~776 chars, > 500-char min window
        var tool = new PrDiffTool(big);
        var first = tool.Page(file: null, offset: 10, limit: 500);
        Assert.Contains("offset: 510", first);
        var last = tool.Page(file: null, offset: 510, limit: 500);
        Assert.Contains("end of whole diff", last);
    }

    [Fact]
    public void UnknownFile_PointsAtManifest()
    {
        var tool = new PrDiffTool(Diff);
        Assert.Contains("not in the diff manifest", tool.Page("Nope.cs", 0, 1000));
    }

    [Fact]
    public void FormatDiffForPrompt_SmallDiff_PassesThrough()
    {
        Assert.Equal(Diff, ReviewerDispatcher.FormatDiffForPrompt(Diff));
    }

    [Fact]
    public void FormatDiffForPrompt_LargeDiff_WholeFilesPlusManifest()
    {
        // Three ~15KB files over the 30KB inline budget: two inline,
        // the third deferred with an explicit manifest entry.
        string File(string name, char fill) =>
            $"diff --git a/{name} b/{name}\n" + new string(fill, 15_000) + "\n";
        var big = File("One.cs", 'a') + File("Two.cs", 'b') + File("Three.cs", 'c');

        var formatted = ReviewerDispatcher.FormatDiffForPrompt(big);

        Assert.Contains("One.cs", formatted);
        Assert.Contains("Two.cs", formatted);
        Assert.Contains("omitted for size", formatted);
        Assert.Contains("pr_diff", formatted);
        Assert.Contains("Three.cs", formatted); // in the manifest
        // The omitted file's CONTENT is not inlined.
        Assert.DoesNotContain(new string('c', 15_000), formatted);
    }
}
