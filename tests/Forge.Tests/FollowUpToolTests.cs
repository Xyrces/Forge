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
    public async Task BlocksParam_CreatesDependencyEdge_AndGatesTarget()
    {
        // Operator model 2026-07-31, case A: a blocking discovery is
        // marked as a real dependency at filing time — the blocked
        // work gates on it (IsBlockedAsync) and the assembler injects
        // the follow-up as "unblocks ongoing work" once groomed.
        var source = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "original"));
        var inFlight = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "in-flight feature"));
        var tool = new FollowUpTool(_issues, source.Id, "CoreDev");

        var fn = tool.AsAIFunction();
        var result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["title"] = "Fix shared-component bug breaking the feature",
            ["description"] = "Found while working: SharedWidget.X null-refs when called from the feature path.",
            ["blocksIssueId"] = inFlight.Id,
        });
        var text = result?.ToString() ?? "";

        Assert.Contains($"blocks {inFlight.Id}", text);
        var filedId = text["filed:".Length..].Split(' ')[0];
        var deps = await _issues.DependenciesAsync(inFlight.Id);
        var edge = Assert.Single(deps.Where(d => d.Kind == IssueDepKind.Blocks));
        Assert.Equal(filedId, edge.BlockedId == inFlight.Id ? edge.BlockerId : edge.BlockedId);
        Assert.True(await _issues.IsBlockedAsync(inFlight.Id));
    }

    [Fact]
    public async Task BlocksParam_UnknownTarget_FilesWithoutEdge()
    {
        var source = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "original"));
        var tool = new FollowUpTool(_issues, source.Id, "CoreDev");

        var fn = tool.AsAIFunction();
        var result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["title"] = "Finding",
            ["description"] = "Something worth doing.",
            ["blocksIssueId"] = "task-9999",
        });
        var text = result?.ToString() ?? "";

        Assert.Contains("not found", text);
        var filedId = text["filed:".Length..].Split(' ')[0];
        Assert.NotNull(await _issues.GetAsync(filedId));
    }

    [Fact]
    public async Task NoBlocksParam_NoEdge()
    {
        var source = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "original"));
        var tool = new FollowUpTool(_issues, source.Id, "CoreDev");

        var fn = tool.AsAIFunction();
        var result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["title"] = "Deferred debt",
            ["description"] = "Nice to have.",
        });
        var text = result?.ToString() ?? "";
        var filedId = text["filed:".Length..].Split(' ')[0];

        Assert.DoesNotContain("blocks", text);
        Assert.Empty(await _issues.DependenciesAsync(filedId));
    }

    [Fact]
    public async Task DraftPath_TracksInsteadOfCreatingTask()
    {
        // Operator model 2026-07-31: deferred findings are TRACKED
        // (drafts) until sprint completion — no live task, no
        // followUps audit mutation mid-sprint.
        var source = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "original"));
        var drafts = new FollowUpDraftStore(_issues);
        var tool = new FollowUpTool(_issues, source.Id, "Reviewer",
            drafts, activeSprintId: _ => Task.FromResult<string?>("sprint-x"));

        var fn = tool.AsAIFunction();
        var result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["title"] = "Deferred finding",
            ["description"] = "Out of scope but real.",
        });
        var text = result?.ToString() ?? "";

        Assert.StartsWith("tracked:draft-", text);
        var open = await drafts.ListUnconsumedAsync();
        var draft = Assert.Single(open);
        Assert.Equal("Deferred finding", draft.Title);
        Assert.Equal("sprint-x", draft.SprintId);
        Assert.Equal(source.Id, draft.SourceIssueId);
        // No task row created.
        Assert.Empty((await _issues.ListAsync(new IssueFilter())).Where(i => i.Title == "Deferred finding"));
    }

    [Fact]
    public async Task BlockerBypass_CreatesTaskEvenWithDraftStore()
    {
        var source = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "original"));
        var inFlight = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "in-flight"));
        var drafts = new FollowUpDraftStore(_issues);
        var tool = new FollowUpTool(_issues, source.Id, "CoreDev",
            drafts, activeSprintId: _ => Task.FromResult<string?>("sprint-x"));

        var fn = tool.AsAIFunction();
        var result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["title"] = "Blocking discovery",
            ["description"] = "The feature cannot proceed without this.",
            ["blocksIssueId"] = inFlight.Id,
        });
        var text = result?.ToString() ?? "";

        Assert.StartsWith("filed:", text);
        Assert.True(await _issues.IsBlockedAsync(inFlight.Id));
        Assert.Empty(await drafts.ListUnconsumedAsync());
    }

    [Fact]
    public async Task BlockerOfSprintMember_BornIntoActiveSprint()
    {
        // Operator rule 2026-07-31: a genuine blocker of ACTIVE
        // sprint work starts inside the sprint (no assembler-tick
        // window where it sits sprint-less).
        var source = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "original"));
        var inFlight = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "in-flight"));
        var sprints = new SprintStore(_issues);
        var sprint = await sprints.CreateAsync(new NewSprint(
            Name: "S", Goal: "g", StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(7)));
        await sprints.AddIssueAsync(sprint.Id, inFlight.Id);
        var tool = new FollowUpTool(_issues, source.Id, "CoreDev", sprints: sprints);

        var fn = tool.AsAIFunction();
        var result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["title"] = "Blocking discovery",
            ["description"] = "The feature cannot proceed without this.",
            ["blocksIssueId"] = inFlight.Id,
        });
        var text = result?.ToString() ?? "";

        var filedId = text["filed:".Length..].Split(' ')[0];
        Assert.Contains(filedId, await sprints.GetIssueIdsAsync(sprint.Id));
        Assert.Equal(sprint.Id, (await _issues.GetAsync(filedId))!.GetMetadata("sprintId"));
    }

    [Fact]
    public async Task BlockerOfNonSprintWork_StaysOutsideSprint()
    {
        var source = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "original"));
        var outside = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "outside"));
        var sprints = new SprintStore(_issues);
        var sprint = await sprints.CreateAsync(new NewSprint(
            Name: "S", Goal: "g", StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(7)));
        var tool = new FollowUpTool(_issues, source.Id, "CoreDev", sprints: sprints);

        var fn = tool.AsAIFunction();
        var result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["title"] = "Blocking discovery",
            ["description"] = "Blocks non-sprint work.",
            ["blocksIssueId"] = outside.Id,
        });
        var text = result?.ToString() ?? "";

        var filedId = text["filed:".Length..].Split(' ')[0];
        Assert.DoesNotContain(filedId, await sprints.GetIssueIdsAsync(sprint.Id));
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
