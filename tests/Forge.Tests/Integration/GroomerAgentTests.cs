using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration.TestHelpers;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Phase 3.1 tests:
/// - GroomerAgent.GroomAsync with a scripted chat client that calls
///   create_story + create_task + set_spec_status in sequence.
/// - DeterministicScorer pure-function tests.
/// </summary>
public class GroomerAgentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;

    public GroomerAgentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-groomer-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private GroomerAgent BuildAgent(IChatClient client)
    {
        var factory = new ScriptingChatClientFactory(client);
        var config = new LlmConfig(new ProviderConfig("test", "", null, null, "test-model"));
        var events = new InMemoryDashboardEventBus();
        return new GroomerAgent(_issues, _specs, events, factory, config,
            NullLogger<GroomerAgent>.Instance, runId: "test-run");
    }

    [Fact]
    public async Task GroomAsync_ApprovedSpec_AgentMovesSpecToGrooming()
    {
        // Create an Approved spec.
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T",
            Body: """
                ## Acceptance criteria
                - [ ] one
                - [ ] two
                - [ ] three
                """));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved);

        // Scripted: agent calls set_spec_status("Grooming"). The
        // story/task chain is harder to script without the agent
        // extracting a real id from create_story's response, so
        // this test focuses on the status transition + a single
        // create_story call to verify the tool works.
        var fcs = new[]
        {
            new FunctionCallContent("c1", "create_story",
                new Dictionary<string, object?> { ["title"] = "Story 1" }),
            new FunctionCallContent("c2", "set_spec_status",
                new Dictionary<string, object?> { ["status"] = "Grooming" }),
        };
        var scripted = new MultiToolCallingChatClient(fcs, "Done.");
        var agent = BuildAgent(scripted);
        var result = await agent.GroomAsync(spec.Id, default);

        // The agent created at least one story; we don't assert on
        // the linked tasks because the scripted client can't model
        // "use the result of the previous call as input to this one."
        Assert.NotNull(result);
        Assert.Single(result!);

        // Spec moved to Grooming.
        var refreshed = await _specs.GetAsync(spec.Id);
        Assert.Equal(SpecStatus.Grooming, refreshed!.Status);
    }

    [Fact]
    public async Task GroomAsync_NotApproved_ReturnsNull()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T", Body: "draft"));
        // Default status is Draft; not Approved.
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "no-op")));
        var agent = BuildAgent(scripted);
        var result = await agent.GroomAsync(spec.Id, default);
        Assert.Null(result);
    }

    [Fact]
    public async Task GroomAsync_UnknownSpec_ReturnsNull()
    {
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var agent = BuildAgent(scripted);
        var result = await agent.GroomAsync("spec-missing", default);
        Assert.Null(result);
    }
}

public class DeterministicScorerTests
{
    [Fact]
    public void Score_EmptyBacklog_ReturnsEmpty()
    {
        var scorer = new DeterministicScorer();
        var result = scorer.Score(Array.Empty<IssueRecord>(), theme: null,
            taskIdsInOtherSprints: new HashSet<string>());
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Score_Priority1Gets10()
    {
        var scorer = new DeterministicScorer();
        var task = MakeTask("dev-1", priority: 1, title: "T", createdAt: DateTime.UtcNow);
        var result = scorer.Score(new[] { task }, theme: null,
            taskIdsInOtherSprints: new HashSet<string>());
        Assert.Single(result.Items);
        Assert.Equal(10, result.Items[0].Score);
        Assert.Contains(result.Items[0].Breakdown, b => b.Contains("priority=1"));
    }

    [Fact]
    public void Score_Priority5Gets1()
    {
        var scorer = new DeterministicScorer();
        var task = MakeTask("dev-5", priority: 5, title: "T", createdAt: DateTime.UtcNow);
        var result = scorer.Score(new[] { task }, theme: null,
            taskIdsInOtherSprints: new HashSet<string>());
        Assert.Equal(1, result.Items[0].Score);
    }

    [Fact]
    public void Score_ThemeSubstringMatch_Adds5()
    {
        var scorer = new DeterministicScorer();
        var task = MakeTask("d-1", priority: 1, title: "Auth polish: clear sessions",
            createdAt: DateTime.UtcNow);
        var result = scorer.Score(new[] { task }, theme: "auth",
            taskIdsInOtherSprints: new HashSet<string>());
        // priority 1 = 10, theme match = 5
        Assert.Equal(15, result.Items[0].Score);
        Assert.Contains(result.Items[0].Breakdown, b => b.Contains("theme-match"));
    }

    [Fact]
    public void Score_TaskInOtherSprint_Loses20()
    {
        var scorer = new DeterministicScorer();
        var task = MakeTask("d-1", priority: 1, title: "T", createdAt: DateTime.UtcNow);
        var result = scorer.Score(new[] { task }, theme: null,
            taskIdsInOtherSprints: new HashSet<string> { "d-1" });
        // priority 1 = 10, penalty = -20
        Assert.Equal(-10, result.Items[0].Score);
        Assert.Contains(result.Items[0].Breakdown, b => b.Contains("in-other-sprint"));
    }

    [Fact]
    public void Score_AgeDaysAddsUpTo10()
    {
        var scorer = new DeterministicScorer();
        var old = MakeTask("d-old", priority: 1, title: "T",
            createdAt: DateTime.UtcNow.AddDays(-7));
        var veryOld = MakeTask("d-vold", priority: 1, title: "T",
            createdAt: DateTime.UtcNow.AddDays(-30));
        var result = scorer.Score(new[] { old, veryOld }, theme: null,
            taskIdsInOtherSprints: new HashSet<string>());
        // 7 days * 2 = 14, capped at 10. 30 days * 2 = 60, capped at 10.
        var oldScored = result.Items.First(i => i.Task.Id == "d-old");
        var veryOldScored = result.Items.First(i => i.Task.Id == "d-vold");
        Assert.Equal(10 + 10, oldScored.Score);   // 10 priority + 10 age
        Assert.Equal(10 + 10, veryOldScored.Score); // 10 priority + 10 age (capped)
    }

    [Fact]
    public void Score_ResultsSortedDescending()
    {
        var scorer = new DeterministicScorer();
        var low = MakeTask("low", priority: 5, title: "T", createdAt: DateTime.UtcNow);
        var mid = MakeTask("mid", priority: 2, title: "T", createdAt: DateTime.UtcNow);
        var high = MakeTask("high", priority: 1, title: "T", createdAt: DateTime.UtcNow);
        var result = scorer.Score(new[] { low, mid, high }, theme: null,
            taskIdsInOtherSprints: new HashSet<string>());
        Assert.Equal("high", result.Items[0].Task.Id);
        Assert.Equal("mid", result.Items[1].Task.Id);
        Assert.Equal("low", result.Items[2].Task.Id);
    }

    private static IssueRecord MakeTask(string id, int priority, string title, DateTime createdAt)
        => new(id, id, "task", title, null, IssueStatus.Pending, priority, null,
            createdAt, createdAt, null, "{}");
}