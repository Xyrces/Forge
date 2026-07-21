using Forge.Agents;
using Forge.Core;
using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Tests for the static prompt + PR-body builders in
/// <see cref="OrchestratorAgent"/>. These methods define the contract
/// for what the LLM sees and what the PR description contains; they
/// are <c>internal</c> so the test project can reach them via
/// <c>InternalsVisibleTo</c>.
/// </summary>
public class OrchestratorAgentPromptTests
{
    private static IssueRecord MakeIssue(string type = "dev", string id = "dev-1", string title = "Add X", string? description = "do the thing")
        => new(id, "1", type, title, description, IssueStatus.Pending, 2, null,
            DateTime.UtcNow, DateTime.UtcNow, null, "{}");

    private static RoleAgent MakeRole() => new(
        AgentName: "coredev",
        ProjectSubdir: "Forge",
        AllowedTools: new List<string> { "dotnet", "git", "gh" });

    [Fact]
    public void BuildPrompt_IncludesRoleAndBranch()
    {
        var prompt = OrchestratorAgent.BuildPrompt(
            MakeIssue(), MakeRole(),
            worktreePath: @"C:\wt\dev-1", branch: "agent/dev-1", defaultBranch: "main");

        Assert.Contains("coredev", prompt, StringComparison.Ordinal);
        Assert.Contains(@"C:\wt\dev-1", prompt, StringComparison.Ordinal);
        Assert.Contains("agent/dev-1", prompt, StringComparison.Ordinal);
        Assert.Contains("main", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_IncludesTaskMetadata()
    {
        var prompt = OrchestratorAgent.BuildPrompt(
            MakeIssue(type: "dev", id: "dev-42", title: "Fix bug Y", description: "details here"),
            MakeRole(), "wt", "agent/dev-42", "main");

        Assert.Contains("dev-42", prompt, StringComparison.Ordinal);
        Assert.Contains("Fix bug Y", prompt, StringComparison.Ordinal);
        // The BuildPrompt does NOT inline the issue description: the agent
        // sees the description via the worktree's existing files and the
        // dashboard's task view. We assert the description is intentionally
        // absent.
        Assert.DoesNotContain("details here", prompt, StringComparison.Ordinal);
        Assert.Contains("Type: dev", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_ListsAllowedTools()
    {
        var prompt = OrchestratorAgent.BuildPrompt(
            MakeIssue(), MakeRole(), "wt", "branch", "main");

        Assert.Contains("dotnet", prompt, StringComparison.Ordinal);
        Assert.Contains("git", prompt, StringComparison.Ordinal);
        Assert.Contains("gh", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_TellsAgentNotToOpenPr()
    {
        // The orchestrator opens the PR; the agent must commit + push only.
        // This contract prevents the agent and orchestrator from racing on
        // PR creation.
        var prompt = OrchestratorAgent.BuildPrompt(
            MakeIssue(), MakeRole(), "wt", "branch", "main");
        Assert.Contains("Do NOT open a PR", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_CommitsWithTaskPrefix()
    {
        var prompt = OrchestratorAgent.BuildPrompt(
            MakeIssue(id: "dev-99", title: "X"), MakeRole(), "wt", "branch", "main");
        Assert.Contains("Task(dev-99):", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrBody_IncludesShaAndResponse()
    {
        var body = OrchestratorAgent.BuildPrBody(
            MakeIssue(id: "dev-5", title: "Add Z", description: "long desc"),
            MakeRole(), sha: "abc123def", response: "I added the file.");

        Assert.Contains("dev-5", body, StringComparison.Ordinal);
        Assert.Contains("abc123def", body, StringComparison.Ordinal);
        Assert.Contains("I added the file.", body, StringComparison.Ordinal);
        Assert.Contains("long desc", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrBody_TruncatesLongResponse()
    {
        var longResponse = new string('x', 1000);
        var body = OrchestratorAgent.BuildPrBody(
            MakeIssue(), MakeRole(), sha: "s", response: longResponse);

        // Truncation puts "..." at the end and caps at 400 chars.
        Assert.Contains("...", body, StringComparison.Ordinal);
        Assert.DoesNotContain(longResponse, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", 5, "")]
    [InlineData("hi", 5, "hi")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello world", 5, "hello...")]
    [InlineData("abcdef", 3, "abc...")]
    public void Truncate_BehavesAsExpected(string input, int max, string expected)
    {
        Assert.Equal(expected, OrchestratorAgent.Truncate(input, max));
    }
}
