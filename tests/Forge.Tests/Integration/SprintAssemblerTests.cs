using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator.Sprint;
using Forge.Projects;
using Forge.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// SprintAssembler: the automatic sprint flow. ALL engineering work
/// happens inside a sprint; the assembler completes the Active sprint
/// when its tasks are terminal and assembles + activates the next
/// one from eligible Pending work (groomed-spec groups FIFO, ad-hoc
/// last). Tests drive TickProjectAsync directly with real stores.
/// </summary>
public class SprintAssemblerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly SprintStore _sprints;
    private readonly SpecStore _specs;
    private readonly InMemoryDashboardEventBus _events;
    private readonly SprintAssembler _assembler;

    public SprintAssemblerTests()
    {
        // Work-dir pattern (not a bare file in the temp root): the
        // sqlite -wal/-shm companions must be cleaned too — 44k
        // leaked ph-*.db-wal files once filled /tmp (22G) and made
        // the whole suite fail with 'disk I/O error'.
        var workDir = Path.Combine(Path.GetTempPath(), $"ph-sprint-asm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        _dbPath = Path.Combine(workDir, "issues.db");
        _workDir = workDir;
        _issues = new IssueStore(_dbPath);
        _sprints = new SprintStore(_issues);
        _specs = new SpecStore(_issues);
        _events = new InMemoryDashboardEventBus();
        _assembler = new SprintAssembler(
            new ProjectContextFactory(new List<ProjectOptions>()),
            _events, NullLogger<SprintAssembler>.Instance);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private Task Tick() => _assembler.TickProjectAsync("test", _issues, _sprints, _specs, CancellationToken.None);

    private async Task<(string specId, List<string> storyIds, List<string> taskIds)> SeedGroomedSpecAsync(
        string title, int taskCount, string? epicDescription = null)
    {
        var epic = await _issues.CreateAsync(new NewIssue(
            Type: "epic", Title: $"Epic: {title}", Description: epicDescription));
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "test", Title: title, Body: "body", ParentIssueId: epic.Id));
        var storyIds = new List<string>();
        var taskIds = new List<string>();
        for (var i = 0; i < taskCount; i++)
        {
            var story = await _issues.CreateAsync(new NewIssue(
                Type: "story", Title: $"{title} story {i}", ParentId: spec.Id));
            var task = await _issues.CreateAsync(new NewIssue(
                Type: "task", Title: $"{title} task {i}", ParentId: story.Id));
            storyIds.Add(story.Id);
            taskIds.Add(task.Id);
        }
        return (spec.Id, storyIds, taskIds);
    }

    [Fact]
    public async Task AssemblesActiveSprint_FromGroomedSpecGroup()
    {
        var (specId, storyIds, taskIds) = await SeedGroomedSpecAsync("Health endpoints", 3,
            epicDescription: "Ship the health/meta endpoint set.");

        await Tick();

        var active = await _sprints.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Equal("Health endpoints", active.Name);
        Assert.Equal("Ship the health/meta endpoint set.", active.Goal);

        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.Equal(taskIds.Count + storyIds.Count, members.Count); // tasks + stories linked
        foreach (var id in taskIds.Concat(storyIds)) Assert.Contains(id, members);
        Assert.DoesNotContain(specId, members); // the spec id itself is never linked (it's not an issue)
    }

    [Fact]
    public async Task NoAssembly_WhileActiveSprintHasOpenTasks()
    {
        var (_, _, taskIds) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var first = await _sprints.GetActiveAsync();
        Assert.NotNull(first);

        await SeedGroomedSpecAsync("Sprint B", 2);
        await Tick();

        var active = await _sprints.GetActiveAsync();
        Assert.Equal(first.Id, active!.Id); // unchanged — Sprint A still open
    }

    [Fact]
    public async Task CompletesSprint_WhenMembersTerminal_ThenAssemblesNext()
    {
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 2);
        await Tick();
        var first = await _sprints.GetActiveAsync();
        Assert.NotNull(first);

        await SeedGroomedSpecAsync("Sprint B", 1);
        foreach (var id in aTasks)
        {
            await _issues.TransitionAsync(id, IssueStatus.Completed, null);
        }
        await Tick();

        var all = await _sprints.ListAsync(activeOnly: false);
        Assert.Equal(2, all.Count);
        Assert.Equal(SprintStatus.Completed, all.Single(s => s.Id == first.Id).Status);
        var active = await _sprints.GetActiveAsync();
        Assert.NotEqual(first.Id, active!.Id);
        Assert.Equal("Sprint B", active.Name);
    }

    [Fact]
    public async Task AdHocTask_GetsOwnSprint_AfterSpecGroups()
    {
        await SeedGroomedSpecAsync("Pipeline work", 1);
        // Ad-hoc tasks need technical grooming before sprint ingest
        // (operator rule 2026-07-23): the groomed marker stands in
        // for the ScheduledGroomer's ad-hoc pass here.
        var adhoc = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "operator one-off",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));

        await Tick(); // spec group wins
        var first = await _sprints.GetActiveAsync();
        Assert.Equal("Pipeline work", first!.Name);

        // Complete the first sprint's tasks; next tick assembles ad-hoc.
        foreach (var id in await _sprints.GetIssueIdsAsync(first.Id))
        {
            var issue = await _issues.GetAsync(id);
            if (issue?.Type == "task")
                await _issues.TransitionAsync(id, IssueStatus.Completed, null);
        }
        await Tick();

        var active = await _sprints.GetActiveAsync();
        Assert.Equal(SprintAssembler.AdHocGroupName, active!.Name);
        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.Contains(adhoc.Id, members);
    }

    [Fact]
    public async Task UngroomedAdHocTask_IsNeverIngested()
    {
        // Operator rule 2026-07-23: no task enters a sprint without
        // technical grooming. An ad-hoc Pending task with no
        // groomed marker blocks nothing — the assembler simply
        // sees no eligible work.
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "ungroomed one-off"));
        await Tick();
        Assert.Null(await _sprints.GetActiveAsync());

        // Grooming marks it; next tick it is assembled.
        await _issues.TransitionAsync(task.Id, IssueStatus.Pending, null,
            metadata: new Dictionary<string, object> { ["groomed"] = "true" });
        await Tick();
        var active = await _sprints.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Contains(task.Id, await _sprints.GetIssueIdsAsync(active!.Id));
    }

    [Fact]
    public async Task SprintGate_Held_CompletesActiveButAssemblesNothing()
    {
        var gates = NewGates();
        var gated = new SprintAssembler(
            new ProjectContextFactory(new List<ProjectOptions>()),
            _events, NullLogger<SprintAssembler>.Instance, gates: gates);
        Task GatedTick() => gated.TickProjectAsync("test", _issues, _sprints, _specs, CancellationToken.None);

        await SeedGroomedSpecAsync("Pipeline work", 1);
        await gates.HoldAsync(StageGates.Sprint);

        // Held: no assembly, even with eligible work.
        await GatedTick();
        Assert.Null(await _sprints.GetActiveAsync());

        // Completion is bookkeeping, not a gated decision: an active
        // sprint whose tasks are terminal still completes under a
        // held gate — but nothing new is started.
        var manual = await _sprints.CreateAsync(new NewSprint(
            Name: "manual", Goal: "g", StartDate: DateTime.UtcNow,
            EndDate: DateTime.UtcNow.AddDays(1), Status: SprintStatus.Active));
        await GatedTick();
        var all = await _sprints.ListAsync(activeOnly: false);
        Assert.Equal(SprintStatus.Completed, all.Single(s => s.Id == manual.Id).Status);
        Assert.Null(await _sprints.GetActiveAsync());

        // Released: the eligible work assembles on the next tick.
        await gates.ReleaseAsync(StageGates.Sprint);
        await GatedTick();
        Assert.NotNull(await _sprints.GetActiveAsync());
    }

    private StageGates NewGates()
    {
        var bootstrap = new IssueStore(Path.Combine(_workDir, "memory.db"));
        bootstrap.Dispose();
        return new StageGates(new MemoryStore(Path.Combine(_workDir, "memory.db")));
    }

    [Fact]
    public async Task ContainersWatchesAndSprintedTasks_AreNeverIngested()
    {
        var epic = await _issues.CreateAsync(new NewIssue(Type: "epic", Title: "container"));
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "container"));
        var watch = await _issues.CreateAsync(new NewIssue(Type: "pr-watch", Title: "watch"));

        await Tick();
        Assert.Null(await _sprints.GetActiveAsync()); // nothing eligible

        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "real",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        await Tick();
        var active = await _sprints.GetActiveAsync();
        Assert.NotNull(active);
        var members = await _sprints.GetIssueIdsAsync(active!.Id);
        Assert.DoesNotContain(epic.Id, members);
        Assert.DoesNotContain(story.Id, members);
        Assert.DoesNotContain(watch.Id, members);

        // Complete it; the same tasks must not be re-ingested into a
        // later sprint.
        await _issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        await Tick();
        Assert.Null(await _sprints.GetActiveAsync()); // nothing new eligible
        Assert.Single(await _sprints.ListAsync(activeOnly: false));
    }

    [Fact]
    public async Task RequeuedTask_FromCompletedSprint_IsReassembled()
    {
        // Operator requeue flow (observed live 2026-07-24, task-158):
        // a task fails, its sprint completes (all members terminal),
        // the operator requeues it to Pending. Completed-sprint
        // membership must NOT strand it — it is definitionally
        // requeued work, not history to protect from resurrection
        // (terminal tasks are already excluded by the Pending filter).
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "real",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        await Tick();
        var first = await _sprints.GetActiveAsync();
        Assert.NotNull(first);

        // Fail the task; the sprint completes (all terminal).
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "boom");
        await Tick();
        Assert.Null(await _sprints.GetActiveAsync());

        // Operator requeue: Failed -> Pending. Next tick must assemble
        // a NEW sprint containing it (previously: stranded forever).
        await _issues.TransitionAsync(task.Id, IssueStatus.Pending, "operator requeue");
        await Tick();
        var second = await _sprints.GetActiveAsync();
        Assert.NotNull(second);
        Assert.NotEqual(first!.Id, second!.Id);
        Assert.Contains(task.Id, await _sprints.GetIssueIdsAsync(second.Id));
    }

    [Fact]
    public async Task EmptySprint_IsImmediatelyCompleted_AndDoesNotBlockAssembly()
    {
        // Defensive: an active sprint with no task members (e.g. only
        // stories linked) is complete by definition.
        var empty = await _sprints.CreateAsync(new NewSprint(
            Name: "empty", Goal: "g", StartDate: DateTime.UtcNow,
            EndDate: DateTime.UtcNow.AddDays(1), Status: SprintStatus.Active));

        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "real",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        await Tick();

        var all = await _sprints.ListAsync(activeOnly: false);
        Assert.Equal(SprintStatus.Completed, all.Single(s => s.Id == empty.Id).Status);
        var active = await _sprints.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Contains(task.Id, await _sprints.GetIssueIdsAsync(active!.Id));
    }
}
