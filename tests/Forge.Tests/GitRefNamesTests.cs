using System.Diagnostics;
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
    public void AtBraceSequence_NoAtBraceInOutput()
    {
        // '@' is already in the invalid-chars set and gets replaced,
        // so '@{' cannot appear in the output (the '@' is gone).
        var result = GitRefNames.Sanitize("foo@{bar");
        Assert.DoesNotContain("@{", result);
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

    [Fact]
    public void DotLockSuffix_Replaced()
    {
        // A component ending in ".lock" is rejected by git-check-ref-format.
        var result = GitRefNames.Sanitize("task.lock");
        Assert.Equal("task_lock", result);
    }

    [Fact]
    public void DotLockSuffix_MultipleDots_PreservesMiddleDots()
    {
        var result = GitRefNames.Sanitize("a.b.lock");
        // "a.b.lock" -> after second pass "a.b.lock" (no .. / leading/trailing dots),
        // then third pass replaces trailing ".lock" -> "a.b_lock"
        Assert.Equal("a.b_lock", result);
    }

    [Fact]
    public void DotLockInMiddle_Preserved()
    {
        // ".lock" not at the end of the string — not prohibited.
        var result = GitRefNames.Sanitize("task.lockdown");
        Assert.Equal("task.lockdown", result);
    }

    [Fact]
    public void DotLockSuffix_WithDotDotSequence()
    {
        // "..lock" -> after second pass "__lock" (.. replaced),
        // "__lock" does not end with ".lock" -> unchanged.
        var result = GitRefNames.Sanitize("..lock");
        Assert.Equal("__lock", result);
    }

    [Fact]
    public void JustLock_NoDotPrefix_PassesThrough()
    {
        // "lock" without a dot prefix is fine.
        var result = GitRefNames.Sanitize("lock");
        Assert.Equal("lock", result);
    }

    /// <summary>
    /// Sweeps every git-check-ref-format edge case listed in the task:
    /// .lock suffix, @{, .., leading/trailing dot, control chars, space, ~^:?*[\
    /// and verifies the output could be used as a git branch ref.
    /// Uses the real <c>git check-ref-format --branch</c> command.
    /// </summary>
    [Fact]
    public void AllGitCheckRefFormatEdgeCases_PassRealGitValidation()
    {
        var inputs = new (string Input, string Description)[]
        {
            ("task.lock",       ".lock suffix"),
            ("foo@{bar",        "@{ sequence"),
            ("..a",             "leading .."),
            ("a..",             "trailing .."),
            (".foo",            "leading dot"),
            ("foo.",            "trailing dot"),
            (".",               "lone dot"),
            ("a\0b",            "null char"),
            ("a\x07b",          "bell char"),
            ("a\tb",            "tab char"),
            ("a b",             "space"),
            ("a~b",             "tilde"),
            ("a^b",             "caret"),
            ("a:b",             "colon"),
            ("a?b",             "question mark"),
            ("a*b",             "asterisk"),
            ("a[b",             "open bracket"),
            ("a\\b",            "backslash"),
            ("a@b",             "at sign"),
            ("a!b",             "exclamation"),
            ("a...b",           "triple dot"),
            ("foo.lock.bar",    "dot lock in middle"),
            ("lock",            "just lock (no dot prefix)"),
            ("a.lock",          "simple .lock"),
            ("a.b.lock",        "nested .lock"),
        };

        foreach (var (input, description) in inputs)
        {
            var sanitized = GitRefNames.Sanitize(input);
            var refName = $"refs/heads/agent/{sanitized}";

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"check-ref-format --branch {refName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // Guard against an inherited CWD that has already been deleted
                // (e.g., by TempRoot cleanup). Git fails with "Unable to read
                // current working directory" when spawned into a removed dir.
                WorkingDirectory = AppContext.BaseDirectory,
            };

            using var proc = Process.Start(psi);
            proc!.WaitForExit(5000);

            Assert.True(proc.ExitCode == 0,
                $"FAIL: input '{input}' ({description}) sanitized to '{sanitized}' " +
                $"which git check-ref-format rejected (exit {proc.ExitCode}): " +
                $"{proc.StandardError.ReadToEnd().Trim()}");
        }
    }
}
