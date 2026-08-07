using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Orchestrator.Workflow;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// P3 checkpoint 2: ClaimExecutor wraps IIssueStore.ClaimAsync
/// with the workflow executor shape so it can be chained via
/// WorkflowBuilder. Tests assert Ok and AlreadyClaimed paths.
/// </summary>
public class ClaimExecutorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;

    public ClaimExecutorTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("claim");
        _issues = new IssueStore(_dbPath);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Claim_NewIssue_ReturnsOkAndResolvesBranch()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var result = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);

        Assert.Equal(ClaimResult.Ok, result.Result);
        Assert.Equal(issue.Id, result.Issue.Id);
        Assert.Equal($"agent/{issue.Id}", result.Branch);
        Assert.Null(result.WorktreePath);
    }

    [Fact]
    public async Task Claim_RespectsExistingBranchMetadata()
    {
        var issue = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "x",
            Metadata: new Dictionary<string, object> { ["branch"] = "custom/branch" }));
        var result = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);

        Assert.Equal(ClaimResult.Ok, result.Result);
        Assert.Equal("custom/branch", result.Branch);
    }

    [Fact]
    public async Task Claim_AlreadyClaimed_ReturnsAlreadyClaimed()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        await _issues.ClaimAsync(issue.Id, "someone-else");
        var result = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);

        Assert.Equal(ClaimResult.AlreadyClaimed, result.Result);
        Assert.Equal(issue.Id, result.Issue.Id);
    }

    [Fact]
    public async Task Claim_SetsStatusToInProgress()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);

        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.InProgress, after!.Status);
        // Claim identity is the owning role, not the legacy opaque
        // "forge" literal (operator 2026-08-01).
        Assert.Equal("coredev", after.Assignee);
    }
}