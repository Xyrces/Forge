using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class IntakeQuestionSynthesisTests
{
    [Theory]
    [InlineData("Is **topics + subscriptions only** the right v1 scope?")]
    [InlineData("Should we plan around Testcontainers?")]
    [InlineData("Confirm this follows the existing target matrix?")]
    [InlineData("Do you want queues too?")]
    public void WithYesNoDefault_YesNoShapedEmptyOptions_GetsYesNo(string question)
    {
        var q = new IntakeQuestion(question, Array.Empty<string>()).WithYesNoDefault();
        Assert.Equal(new[] { "Yes", "No" }, q.Options);
    }

    [Fact]
    public void WithYesNoDefault_ExistingOptions_Untouched()
    {
        var q = new IntakeQuestion("Is this right?", new[] { "A", "B" }).WithYesNoDefault();
        Assert.Equal(new[] { "A", "B" }, q.Options);
    }

    [Fact]
    public void WithYesNoDefault_OpenEndedQuestion_StaysFreeForm()
    {
        var q = new IntakeQuestion("What should the package be named?", Array.Empty<string>()).WithYesNoDefault();
        Assert.Empty(q.Options);
    }
}

public class IntakeQuestionParserTests
{
    [Fact]
    public void Parse_NumberedQuestions_WithSubBulletOptions()
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
        Assert.Contains("Transport scope", qs[0].Question);
        Assert.Empty(qs[0].Options);
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
}
