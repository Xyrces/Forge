using Forge.Core;
using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// PR title/body composition (operator rule 2026-08-06, after PR
/// #817 shipped the whole model conversation as its description and
/// others went out as "conversation resumed"): titles carry the task
/// id; bodies are structured, sanitized, and bounded.
/// </summary>
public class PrTextTests
{
    private static IssueRecord Issue(string id = "task-547", string title = "Fix the thing",
        string? description = "The thing is broken in this specific way.")
        => new(Id: id, ShortId: id, Type: "task", Title: title, Description: description,
            Status: IssueStatus.InProgress, Priority: 2, Assignee: null,
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow,
            ClosedAt: null, MetadataJson: "{}");

    [Fact]
    public void Title_CarriesTaskId_AndStaysBounded()
    {
        Assert.Equal("Task(task-547): Fix the thing", PrText.Title(Issue()));

        var longTitle = new string('x', 300);
        var title = PrText.Title(Issue(title: longTitle));
        Assert.True(title.Length <= 121, $"title too long: {title.Length}");
        Assert.StartsWith("Task(task-547): ", title);
    }

    [Fact]
    public void Body_Structured_WithTaskDescriptionAndSummary()
    {
        var body = PrText.Body(Issue(), "abcdef1234567890", "Implemented the fix in World.cs; all tests pass.");

        Assert.Contains("**task-547** — Fix the thing", body);
        Assert.Contains("The thing is broken", body);
        Assert.Contains("## Implementation", body);
        Assert.Contains("Implemented the fix", body);
        Assert.Contains("abcdef12", body);
        Assert.DoesNotContain("abcdef1234567890", body); // sha abbreviated
    }

    [Fact]
    public void Body_ResumeArtifact_FallsBackToDescription_Only()
    {
        var body = PrText.Body(Issue(), "abc123", "Conversation resumed — continuing from where we left off.");

        Assert.DoesNotContain("Conversation resumed", body);
        Assert.DoesNotContain("## Implementation", body);
        Assert.Contains("The thing is broken", body);
    }

    [Fact]
    public void Body_StripsLeakedToolCallMarkup_AndCapsLength()
    {
        var modelText = "Did the work.\n]<]minimax[><tool_call><invoke name=\"bash\">junk</invoke></tool_call>\n" +
            new string('y', 5000);
        var body = PrText.Body(Issue(), "abc123", modelText);

        Assert.Contains("Did the work.", body);
        Assert.DoesNotContain("]<]minimax[", body);
        Assert.DoesNotContain("<tool_call>", body);
        Assert.True(body.Length < 3500, $"body not bounded: {body.Length}");
    }

    [Fact]
    public void Body_EmptyModelText_StillHasTaskSection()
    {
        var body = PrText.Body(Issue(), "abc123", null);
        Assert.Contains("## Task", body);
        Assert.DoesNotContain("## Implementation", body);
    }

    [Fact]
    public void Body_NullSha_OmitsHeadFooter()
    {
        var body = PrText.Body(Issue(), headSha: null, modelText: null, note: "recovered by StartupRecovery after a crash");
        Assert.Contains("_recovered by StartupRecovery after a crash_", body);
        Assert.DoesNotContain("head `", body);
        Assert.Contains("Opened by Forge", body);
    }
}
