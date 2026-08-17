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
        _dbPath = TempRoot.Instance.NewDbPath("groomer");
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
    public async Task GroomAsync_ApprovedSpec_AgentMovesSpecToGroomed()
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

        // Scripted: agent creates a story then moves the spec to
        // Groomed (the intended terminal status after P3.5).
        var fcs = new[]
        {
            new FunctionCallContent("c1", "create_story",
                new Dictionary<string, object?> { ["title"] = "Story 1" }),
            new FunctionCallContent("c2", "set_spec_status",
                new Dictionary<string, object?> { ["status"] = "Groomed" }),
        };
        var scripted = new MultiToolCallingChatClient(fcs, "Done.");
        var agent = BuildAgent(scripted);
        var result = await agent.GroomAsync(spec.Id, default);

        // The agent created at least one story; we don't assert on
        // the linked tasks because the scripted client can't model
        // "use the result of the previous call as input to this one."
        Assert.NotNull(result);
        Assert.Single(result!.StoryIds);

        // Spec moved to Groomed.
        var refreshed = await _specs.GetAsync(spec.Id);
        Assert.Equal(SpecStatus.Groomed, refreshed!.Status);
    }

    [Fact]
    public async Task GroomAsync_AddDependency_WiresBlocksEdge_AndDispatchGatesOnIt()
    {
        // The groomer must wire physical prerequisites (observed live
        // 2026-08-12: talaria Sprint 7 ran the new-project scaffold
        // concurrently with the tasks writing into it). The dispatch
        // queue then holds the dependent until the blocker completes.
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T",
            Body: """
                ## Acceptance criteria
                - [ ] scaffold
                - [ ] implementation
                """));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved);
        var blocker = await _issues.CreateAsync(new NewIssue("task", "scaffold project", null, 2, null));
        var dependent = await _issues.CreateAsync(new NewIssue("task", "implement into it", null, 2, null));

        var fcs = new[]
        {
            new FunctionCallContent("c1", "add_dependency",
                new Dictionary<string, object?>
                {
                    ["blockerId"] = blocker.Id,
                    ["blockedId"] = dependent.Id,
                    ["rationale"] = "creates the project the dependent edits",
                }),
            new FunctionCallContent("c2", "set_spec_status",
                new Dictionary<string, object?> { ["status"] = "Groomed" }),
        };
        var agent = BuildAgent(new MultiToolCallingChatClient(fcs, "Done."));
        await agent.GroomAsync(spec.Id, default);

        // (No create_story was scripted, so GroomAsync returns null —
        // the dependency wiring is the assertion target.)
        Assert.Equal(SpecStatus.Groomed, (await _specs.GetAsync(spec.Id))!.Status);
        var deps = await _issues.DependenciesAsync(dependent.Id);
        Assert.Contains(deps, d => d.BlockerId == blocker.Id && d.Kind == IssueDepKind.Blocks);

        // Dispatch gate: the dependent is NOT ready while the blocker
        // is open, and becomes ready once it completes.
        var ready = await _issues.ReadyAsync(10, default);
        Assert.DoesNotContain(ready, r => r.Id == dependent.Id);
        Assert.Contains(ready, r => r.Id == blocker.Id);

        await _issues.TransitionAsync(blocker.Id, IssueStatus.Completed, null, ct: default);
        ready = await _issues.ReadyAsync(10, default);
        Assert.Contains(ready, r => r.Id == dependent.Id);
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

    [Fact]
    public async Task GroomAsync_BareNumericStoryId_NormalizesToCreatedStory()
    {
        // Live corruption 2026-08-09 (porthorizon spec-257a4c26,
        // surfaced 2026-08-17): the model passed storyId "39" and the
        // task was written with parent_issue_id="39" — the spec tree
        // showed the story as taskless, the story never auto-closed,
        // and the spec stranded with sprints never assembling its work.
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T",
            Body: """
                ## Acceptance criteria
                - [ ] one
                """));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved);

        // The store mints story-1 for the first story; the model
        // references it as bare "1".
        var fcs = new[]
        {
            new FunctionCallContent("c1", "create_story",
                new Dictionary<string, object?> { ["title"] = "Story 1" }),
            new FunctionCallContent("c2", "create_task",
                new Dictionary<string, object?>
                {
                    ["title"] = "Do the thing",
                    ["storyId"] = "1",
                }),
            new FunctionCallContent("c3", "set_spec_status",
                new Dictionary<string, object?> { ["status"] = "Groomed" }),
        };
        var agent = BuildAgent(new MultiToolCallingChatClient(fcs, "Done."));
        var result = await agent.GroomAsync(spec.Id, default);

        var storyId = Assert.Single(result!.StoryIds);
        var tasks = (await _issues.ListAsync(new IssueFilter { Type = "task" }, default)).ToList();
        var task = Assert.Single(tasks);
        Assert.Equal(storyId, task.ParentIssueId);
    }

    [Fact]
    public async Task GroomAsync_HallucinatedStoryId_Rejected_NoTaskCreated()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T",
            Body: """
                ## Acceptance criteria
                - [ ] one
                """));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved);

        var fcs = new[]
        {
            new FunctionCallContent("c1", "create_story",
                new Dictionary<string, object?> { ["title"] = "Story 1" }),
            new FunctionCallContent("c2", "create_task",
                new Dictionary<string, object?>
                {
                    ["title"] = "Do the thing",
                    ["storyId"] = "story-999",
                }),
            new FunctionCallContent("c3", "set_spec_status",
                new Dictionary<string, object?> { ["status"] = "Groomed" }),
        };
        var agent = BuildAgent(new MultiToolCallingChatClient(fcs, "Done."));
        await agent.GroomAsync(spec.Id, default);

        Assert.Empty(await _issues.ListAsync(new IssueFilter { Type = "task" }, default));
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
/// <summary>
/// Multi-project routing (live incident 2026-07-29): a groomer run
/// for a spec owned by project B must write its stories/tasks into
/// project B's issue store — never the default (primary) store,
/// whose sprint lane would dispatch them against the wrong repo.
/// </summary>
public class GroomerRoutingTests : IDisposable
{
    private readonly string _dir;
    private readonly IssueStore _defaultStore;
    private readonly IssueStore _projectStore;
    private readonly SpecStore _specs;

    public GroomerRoutingTests()
    {
        _dir = TempRoot.Instance.NewDirectory("groomroute");
        Directory.CreateDirectory(_dir);
        _defaultStore = new IssueStore(Path.Combine(_dir, "default.db"));
        _projectStore = new IssueStore(Path.Combine(_dir, "proj.db"));
        // The spec store is shared (primary-backed) in production —
        // specs carry project_id, issues are per-project.
        _specs = new SpecStore(_defaultStore);
    }

    public void Dispose()
    {
        try { _defaultStore.Dispose(); } catch { }
        try { _projectStore.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task GroomAsync_SpecOwnedByOtherProject_StoriesLandInThatProjectsStore()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "porthorizon", Title: "Hygiene",
            Body: "## Acceptance criteria\n- [ ] cleanup\n"));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved);

        var fcs = new[]
        {
            new FunctionCallContent("c1", "create_story",
                new Dictionary<string, object?> { ["title"] = "Story routed" }),
            new FunctionCallContent("c2", "set_spec_status",
                new Dictionary<string, object?> { ["status"] = "Groomed" }),
        };
        var scripted = new MultiToolCallingChatClient(fcs, "Done.");
        var chatFactory = new ScriptingChatClientFactory(scripted);
        var config = new LlmConfig(new ProviderConfig("test", "", null, null, "test-model"));
        var factory = new GroomerAgentFactory(
            _defaultStore, _specs, new InMemoryDashboardEventBus(),
            chatFactory, config, NullLoggerFactory.Instance,
            issueStoreLookup: id => id == "porthorizon" ? _projectStore : null);

        var agent = factory.Create(projectId: spec.ProjectId);
        var result = await agent.GroomAsync(spec.Id, default);

        Assert.NotNull(result);
        Assert.Single(result!.StoryIds);

        // The story exists ONLY in the porthorizon store.
        var inProject = await _projectStore.ListAsync(new IssueFilter { Type = "story" }, default);
        Assert.Single(inProject);
        Assert.Equal(spec.Id, inProject[0].ParentIssueId);
        var inDefault = await _defaultStore.ListAsync(new IssueFilter { Type = "story" }, default);
        Assert.Empty(inDefault);
    }

    [Fact]
    public async Task GroomAsync_UnknownProject_FallsBackToDefaultStore()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "ghost", Title: "T", Body: "## Acceptance criteria\n- [ ] x\n"));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved);

        var fcs = new[]
        {
            new FunctionCallContent("c1", "create_story",
                new Dictionary<string, object?> { ["title"] = "S" }),
            new FunctionCallContent("c2", "set_spec_status",
                new Dictionary<string, object?> { ["status"] = "Groomed" }),
        };
        var scripted = new MultiToolCallingChatClient(fcs, "Done.");
        var chatFactory = new ScriptingChatClientFactory(scripted);
        var config = new LlmConfig(new ProviderConfig("test", "", null, null, "test-model"));
        var factory = new GroomerAgentFactory(
            _defaultStore, _specs, new InMemoryDashboardEventBus(),
            chatFactory, config, NullLoggerFactory.Instance,
            issueStoreLookup: _ => null);

        var agent = factory.Create(projectId: spec.ProjectId);
        var result = await agent.GroomAsync(spec.Id, default);

        Assert.NotNull(result);
        var inDefault = await _defaultStore.ListAsync(new IssueFilter { Type = "story" }, default);
        Assert.Single(inDefault);
    }
}

/// <summary>
/// SprintAssembler.DropCrossProjectGroupsAsync: tasks chained to a
/// spec owned by ANOTHER project must never assemble in this
/// project's sprint lane.
/// </summary>
public class SprintAssemblerGuardTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;

    public SprintAssemblerGuardTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("guard");
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private static IssueRecord MakeTask(string id, string? parentId)
        => new(id, id, "task", $"T-{id}", null, IssueStatus.Pending, 2, parentId,
            DateTime.UtcNow, DateTime.UtcNow, null, "{}");

    [Fact]
    public async Task Guard_DropsGroupsOwnedByOtherProjects_KeepsOwnAndAdHoc()
    {
        var foreignSpec = await _specs.CreateAsync(new NewSpec(ProjectId: "porthorizon", Title: "F", Body: "b"));
        var ownSpec = await _specs.CreateAsync(new NewSpec(ProjectId: "forge", Title: "O", Body: "b"));

        var groups = new Dictionary<string, List<IssueRecord>>(StringComparer.Ordinal)
        {
            [foreignSpec.Id] = new() { MakeTask("task-f1", "story-f") },
            [ownSpec.Id] = new() { MakeTask("task-o1", "story-o") },
            [Forge.Orchestrator.Sprint.SprintAssembler.AdHocGroupName] = new() { MakeTask("task-a1", null) },
        };
        var order = groups.Keys.ToList();

        var dropped = await Forge.Orchestrator.Sprint.SprintAssembler.DropCrossProjectGroupsAsync(
            groups, order, projectId: "forge", _specs,
            NullLogger<Forge.Orchestrator.Sprint.SprintAssembler>.Instance, default);

        Assert.Equal(1, dropped);
        Assert.DoesNotContain(foreignSpec.Id, order);
        Assert.Contains(ownSpec.Id, order);
        Assert.Contains(Forge.Orchestrator.Sprint.SprintAssembler.AdHocGroupName, order);
    }
}
