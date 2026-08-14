using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class IntakeQuestionParserTests
{
    [Fact]
    public void Parse_NumberedQuestions_HeadersOptionsAndMultiple()
    {
        // Shape observed live 2026-08-12 (talaria intake): numbered
        // bold-led questions, one with indented option bullets.
        var text = """
            Got it — that's a substantial addition. Before I propose an epic:

            1. **Transport scope** — Do you want a full `ITransport` implementation, or strictly just the transport layer?
            2. **Multi-targeting** — Confirm this should follow the existing target matrix?
            3. **Feature parity** — Which of the existing transport semantics must be preserved?
               - At-least-once with offset-style commit
               - Fencing tokens for idempotency
               - Lease/visibility-timeout handling
               - All of the above

            Once I know this I'll propose the epic.
            """;

        var qs = IntakeQuestionParser.Parse(text);

        Assert.Equal(3, qs.Count);

        Assert.Equal("Transport scope", qs[0].Header);
        Assert.False(qs[0].Multiple);

        Assert.Equal("Multi-targeting", qs[1].Header);

        Assert.Equal("Feature parity", qs[2].Header);
        Assert.True(qs[2].Multiple); // "Which of the existing …"
        Assert.Equal(new[]
        {
            "At-least-once with offset-style commit",
            "Fencing tokens for idempotency",
            "Lease/visibility-timeout handling",
            "All of the above",
        }, qs[2].Options);
    }

    [Fact]
    public void Parse_NoListQuestions_ReturnsEmpty()
    {
        Assert.Empty(IntakeQuestionParser.Parse("Here is a plain reply with a question mark?"));
        Assert.Empty(IntakeQuestionParser.Parse("- a bullet without a question"));
        Assert.Empty(IntakeQuestionParser.Parse(""));
        Assert.Empty(IntakeQuestionParser.Parse(null));
    }

    [Fact]
    public void Parse_BulletQuestions_Recognized()
    {
        var qs = IntakeQuestionParser.Parse("- Which provider should own this?\n- Or should it stay in-process?");

        Assert.Equal(2, qs.Count);
        Assert.Equal("Which provider should own this?", qs[0].Question);
        Assert.Null(qs[0].Header);
    }

    [Fact]
    public void Parse_CapsAtEightQuestions()
    {
        var text = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"{i}. Question number {i}?"));
        Assert.Equal(8, IntakeQuestionParser.Parse(text).Count);
    }

    [Fact]
    public void Parse_SubBulletWithQuestionMark_EndsOptionRun_AndBecomesQuestion()
    {
        var text = "1. First question?\n   - an option\n   - Is this actually the next question?\n   - never reached";
        var qs = IntakeQuestionParser.Parse(text);
        Assert.Equal(2, qs.Count);
        Assert.Single(qs[0].Options);
        Assert.Equal("Is this actually the next question?", qs[1].Question);
        Assert.Equal(new[] { "never reached" }, qs[1].Options);
    }

    [Fact]
    public void Parse_OrBranchQuestion_ExtractsBothChoices()
    {
        // The exact question from the live talaria session that
        // degraded to a bare Yes/No under the old synthesis.
        var qs = IntakeQuestionParser.Parse(
            "1. Is **topics + subscriptions only** (no queues) the right v1 scope, or do you also want point-to-point queues?");

        var q = Assert.Single(qs);
        Assert.Equal("topics + subscriptions only", q.Header);
        Assert.Equal(
            new[] { "Yes — topics + subscriptions only", "Also: Point-to-point queues" },
            q.Options);
    }

    [Fact]
    public void Parse_OrBranchWithoutHeader_UsesAsDescribed()
    {
        var qs = IntakeQuestionParser.Parse("1. Should we keep the current layout, or move it under a subfolder?");

        var q = Assert.Single(qs);
        Assert.Null(q.Header);
        Assert.Equal(new[] { "Yes — as described", "Also: Move it under a subfolder" }, q.Options);
    }

    [Fact]
    public void Parse_PlainYesNo_GetsYesNo()
    {
        var qs = IntakeQuestionParser.Parse("1. Is the emulator for CI acceptable?");

        var q = Assert.Single(qs);
        Assert.Equal(new[] { "Yes", "No" }, q.Options);
    }

    [Fact]
    public void Parse_OpenQuestion_StaysFreeForm()
    {
        var qs = IntakeQuestionParser.Parse("1. What should the package be named?");

        var q = Assert.Single(qs);
        Assert.Empty(q.Options);
        Assert.False(q.Multiple);
    }
}
