using Microsoft.Extensions.AI;
using Forge.AgentTools;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// FollowUpTool: the agent-initiated issue-creation path. Filed
/// follow-ups must land parentless and ungroomed so the sprint
/// assembler ignores them until technical grooming approves them
/// (operator rule 2026-07-23). The source task gets a followUps
/// audit trail.
/// </summary>
public class FollowUpToolTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;

    public FollowUpToolTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-fup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FilesParentlessUngroomedTask_AndAuditsSource()
    {
        var source = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "original"));
        var tool = new FollowUpTool(_issues, source.Id, "Reviewer");

        var fn = tool.AsAIFunction();
        var result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["title"] = "Deferred: tighten null checks in IssueStore",
            ["description"] = "Spotted while reviewing PR #9: IssueStore.X doesn't guard null.",
            ["priority"] = 2,
        });
        var text = result?.ToString() ?? "";

        Assert.StartsWith("filed:", text);
        var filedId = text["filed:".Length..].Split(' ')[0];
        var filed = await _issues.GetAsync(filedId);
        Assert.NotNull(filed);
        Assert.Equal(IssueStatus.Pending, filed!.Status);
        // Parentless + no groomed marker: NOT sprint-eligible.
        Assert.Null(filed.ParentIssueId);
        Assert.NotEqual("true", filed.GetMetadata("groomed"));
        Assert.Equal("Reviewer", filed.GetMetadata("source"));
        Assert.Equal(source.Id, filed.GetMetadata("followUpOf"));
        Assert.Equal(2, filed.Priority);

        var after = await _issues.GetAsync(source.Id);
        Assert.Equal(filed.Id, after!.GetMetadata("followUps"));
    }

    [Fact]
    public async Task RequiresTitleAndDescription()
    {
        var source = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "original"));
        var tool = new FollowUpTool(_issues, source.Id, "CoreDev");
        var fn = tool.AsAIFunction();

        var r1 = await fn.InvokeAsync(new AIFunctionArguments { ["title"] = "", ["description"] = "x" });
        Assert.Equal("title_required", r1?.ToString());
        var r2 = await fn.InvokeAsync(new AIFunctionArguments { ["title"] = "x", ["description"] = "" });
        Assert.Equal("description_required", r2?.ToString());
        Assert.Empty(await _issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }) is var l
            ? l.Where(i => i.Id != source.Id) : l);
    }
}
