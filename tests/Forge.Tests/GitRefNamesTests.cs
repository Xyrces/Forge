using Forge.AgentTools;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Unit tests for <see cref="GitRefNames.Sanitize"/>.
/// Ensures branch/directory names are consistent between
/// GitWorktreeService and WorktreeExecutor.
/// </summary>
public class GitRefNamesTests
{
    [Fact]
    public void NormalId_PassesThroughUnchanged()
    {
        var result = GitRefNames.Sanitize("task-174");
        Assert.Equal("task-174", result);
    }

    [Fact]
    public void NormalId_WithUnderscores_PassesThrough()
    {
        var result = GitRefNames.Sanitize("task_174_abc");
        Assert.Equal("task_174_abc", result);
    }

    [Fact]
    public void Tilde_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo~bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void Caret_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo^bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void Colon_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo:bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void QuestionMark_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo?bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void Asterisk_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo*bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void OpenBracket_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo[bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void Backslash_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo\\bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void Space_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void AtSign_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo@bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void Exclamation_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo!bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void ForwardSlash_ReplacedWithUnderscore()
    {
        var result = GitRefNames.Sanitize("foo/bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void DotDotSequence_Leading_Replaced()
    {
        var result = GitRefNames.Sanitize("..a");
        Assert.Equal("__a", result);
    }

    [Fact]
    public void DotDotSequence_Trailing_Replaced()
    {
        var result = GitRefNames.Sanitize("a..");
        Assert.Equal("a__", result);
    }

    [Fact]
    public void TripleDot_Replaced()
    {
        var result = GitRefNames.Sanitize("...");
        // each dot is part of a ".." sequence with its neighbor
        Assert.Equal("___", result);
    }

    [Fact]
    public void LoneDot_Replaced()
    {
        var result = GitRefNames.Sanitize(".");
        Assert.Equal("_", result);
    }

    [Fact]
    public void SingleDotMiddle_Preserved()
    {
        var result = GitRefNames.Sanitize("a.b");
        Assert.Equal("a.b", result);
    }

    [Fact]
    public void LeadingDot_Replaced()
    {
        var result = GitRefNames.Sanitize(".foo");
        Assert.Equal("_foo", result);
    }

    [Fact]
    public void TrailingDot_Replaced()
    {
        var result = GitRefNames.Sanitize("foo.");
        Assert.Equal("foo_", result);
    }

    [Fact]
    public void LeadingAndTrailingDot_Replaced()
    {
        var result = GitRefNames.Sanitize(".foo.");
        Assert.Equal("_foo_", result);
    }

    [Fact]
    public void DotDotDotMiddle_Replaced()
    {
        var result = GitRefNames.Sanitize("a...b");
        Assert.Equal("a___b", result);
    }

    [Fact]
    public void ControlCharacters_Replaced()
    {
        // Null (0x00), Bell (0x07), Tab (0x09), Escape (0x1B), Delete (0x7F)
        var input = "\0\x07\t\x1B\x7F";
        var result = GitRefNames.Sanitize(input);
        Assert.Equal("_____", result);
    }

    [Fact]
    public void MixedInvalidChars_AllReplaced()
    {
        var result = GitRefNames.Sanitize("a~^:?*[\\ @!b");
        Assert.Equal("a__________b", result);
    }

    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        var result = GitRefNames.Sanitize(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Null_ReturnsEmpty()
    {
        var result = GitRefNames.Sanitize(null!);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void DotDotPrecededByDot_SingleDotPreserved()
    {
        // "a..b.c" -> "a__b.c" (the middle '.' in ".." gets _
        // neighbor; but 'b.c' stays because .c is not a ..)
        var result = GitRefNames.Sanitize("a..b.c");
        Assert.Equal("a__b.c", result);
    }

    [Fact]
    public void DotFollowedByDot_SingleDotPreserved()
    {
        // "a.b..c" -> "a.b__c"
        var result = GitRefNames.Sanitize("a.b..c");
        Assert.Equal("a.b__c", result);
    }
}
