using System.Security.Cryptography;
using System.Text;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class VisionMapTests
{
    [Fact]
    public void Parse_PreservesEveryCharacterAndIgnoresCodeAndNestedHeadings()
    {
        const string source = "\uFEFFPreamble\r\n\r\n# **Forge**\r\nIntro\r\n\r\n"
            + "## Principles\r\n| Rule | Detail |\r\n| --- | --- |\r\n| A | B |\r\n"
            + "\r\n~~~markdown\r\n## This is a code sample\r\n~~~\r\n"
            + "\r\n> ## A quotation\r\n> Its explanation\r\n"
            + "\r\n    ## Indented code\r\n"
            + "\r\n### Details\r\n[Source](https://example.com)\r\n"
            + "\r\n## Principles\r\nRepeated title; distinct node.";

        var map = VisionMap.Parse(source);

        Assert.Equal(source, map.Source);
        Assert.Equal(source, string.Concat(map.Nodes.Select(node => node.Text)));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), map.Revision);
        Assert.Equal(["Forge", "Principles", "Details", "Principles"], map.Nodes.Select(node => node.Title));
        Assert.Equal("root", map.Nodes[0].Id);
        Assert.Null(map.Nodes[0].ParentId);
        Assert.Equal(map.Nodes[1].Id, map.Nodes[2].ParentId);
        Assert.Equal("root", map.Nodes[3].ParentId);
        Assert.NotEqual(map.Nodes[1].Id, map.Nodes[3].Id);
        Assert.Equal(map.Nodes, VisionMap.Parse(source).Nodes);
    }

    [Fact]
    public void Parse_SupportsSetextSkippedLevelsAndDocumentsWithoutAHeading()
    {
        const string source = "Project\n=======\n\nIdentity\n--------\ntext\n\n##### Detail\nmore";
        var map = VisionMap.Parse(source);
        Assert.Equal(["Project", "Identity", "Detail"], map.Nodes.Select(node => node.Title));
        Assert.Equal(map.Nodes[1].Id, map.Nodes[2].ParentId);
        Assert.Equal(source, string.Concat(map.Nodes.Select(node => node.Text)));
        Assert.Equal("Unstructured\rtext", Assert.Single(VisionMap.Parse("Unstructured\rtext").Nodes).Text);
        Assert.Empty(Assert.Single(VisionMap.Parse("").Nodes).Text);
    }

    [Fact]
    public void Apply_UsesOriginalRevisionAndChangesOnlyTheExactOwnedExcerpt()
    {
        const string source = "# Forge\r\n\r\n## Work\r\nWait for all tasks.\r\n\r\n"
            + "### Exceptions\r\nWait for all tasks.\r\n\r\n## Other\r\nRetain this.\r\n";
        var map = VisionMap.Parse(source);
        var node = map.Nodes.Single(node => node.Title == "Work");
        var updated = VisionMap.Apply(source, map.Revision,
            [new(node.Id, "replace", "Wait for all tasks.", "Wait for tasks and acceptance checks.")]);
        Assert.Equal(source.Replace("## Work\r\nWait for all tasks.",
            "## Work\r\nWait for tasks and acceptance checks.", StringComparison.Ordinal), updated);
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply(updated, map.Revision, [new(node.Id, "append", "", "A concurrent change.")]));
    }

    [Fact]
    public void Apply_RejectsAmbiguousCrossNodeAndOverlappingEdits()
    {
        const string source = "# Forge\n\n## Work\nSame. Same.\n\n### Child\nChild text.\n";
        var map = VisionMap.Parse(source);
        var node = map.Nodes.Single(node => node.Title == "Work");
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply(source, map.Revision, [new(node.Id, "replace", "Same.", "Different.")]));
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply(source, map.Revision, [new(node.Id, "replace", "Child text.", "Different.")]));
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply(source, map.Revision, [new(node.Id, "replace", "Same. Same.", "One."),
                new(node.Id, "replace", "Same. Same.", "Two.")]));
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply(source, map.Revision, [new(node.Id, "replace", "## Work", "## Renamed")]));
    }

    [Fact]
    public void Apply_AppendsToOwnContentAndAddsChildrenAfterExistingDescendants()
    {
        const string source = "# Forge\r\n\r\n## Work\r\nExisting.\r\n\r\n"
            + "### Child\r\nChild content.\r\n\r\n## Other\r\nRetain.";
        var map = VisionMap.Parse(source);
        var work = map.Nodes.Single(node => node.Title == "Work");
        var updated = VisionMap.Apply(source, map.Revision,
            [new(work.Id, "append", "", "Added detail."),
                new(work.Id, "add", "", "New child body.", "New child"),
                new(work.Id, "add", "", "Another child body.", "Another child")]);
        Assert.Equal("# Forge\r\n\r\n## Work\r\nExisting.\r\n\r\nAdded detail.\r\n\r\n"
            + "### Child\r\nChild content.\r\n\r\n### New child\r\n\r\nNew child body.\r\n\r\n"
            + "### Another child\r\n\r\nAnother child body.\r\n\r\n## Other\r\nRetain.", updated);
        var after = VisionMap.Parse(updated);
        Assert.Equal(["Child", "New child", "Another child"],
            after.Nodes.Where(node => node.ParentId == after.Nodes.Single(item => item.Title == "Work").Id)
                .Select(node => node.Title));
    }

    [Fact]
    public void Apply_RejectsChangesThatHideOrInjectOtherNodes()
    {
        const string source = "# Forge\n\n## Work\nKeep this.\n\n## Next\nAlso keep this.\n";
        var map = VisionMap.Parse(source);
        var work = map.Nodes.Single(node => node.Title == "Work");
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply(source, map.Revision, [new(work.Id, "append", "", "~~~")]));
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply(source, map.Revision, [new(work.Id, "replace", "Keep this.", "### Hidden addition\nContent")]));
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply(source, map.Revision, [new(work.Id, "add", "", "## Wrong hierarchy", "Child")]));
    }

    [Fact]
    public void Apply_CanCreateAFirstNodeAndRejectsMalformedInsertions()
    {
        var empty = VisionMap.Parse("");
        var source = VisionMap.Apply("", empty.Revision, [new("root", "add", "", "Our purpose.", "Purpose")]);
        var parsed = VisionMap.Parse(source);
        Assert.Equal("root", parsed.Nodes.Single(node => node.Title == "Purpose").ParentId);
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply("", empty.Revision, [new("root", "add", "", "Body", "Two\nlines")]));
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply("", empty.Revision, [new("missing", "append", "", "Body")]));
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply("", empty.Revision, [new("root", "remove", "", "")]));
        Assert.Throws<InvalidOperationException>(() =>
            VisionMap.Apply("", empty.Revision, [new("root", "append", "Stale text", "Body")]));
    }

    [Fact]
    public void Apply_RootAdditionRemainsRootOwnedWhenDocumentContainsMultipleTopLevelHeadings()
    {
        const string source = "# One\n\n## Child\nDetail.\n\n# Two\nSecond root topic.\n";
        var map = VisionMap.Parse(source);
        var updated = VisionMap.Apply(source, map.Revision, [new("root", "add", "", "New body.", "Three")]);
        var added = VisionMap.Parse(updated).Nodes.Single(node => node.Title == "Three");
        Assert.Equal("root", added.ParentId);
        Assert.StartsWith(source, updated, StringComparison.Ordinal);
    }
}
