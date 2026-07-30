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
        var workDir = TempRoot.Instance.NewDirectory("sprint-asm");
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
    public async Task Story_AutoCloses_WhenAllTasksTerminal_ThenEpicFollows()
    {
        // Stories linger Pending when their tasks complete (the
        // 2026-07-27 backfill finding) — the tick closes them, and
        // the epic closes behind them.
        var epic = await _issues.CreateAsync(new NewIssue(Type: "epic", Title: "e"));
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "test", Title: "s", Body: "b", ParentIssueId: epic.Id));
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "st", ParentId: spec.Id));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "t", ParentId: story.Id));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved, CancellationToken.None);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Grooming, CancellationToken.None);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Groomed, CancellationToken.None);

        await _issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        await Tick();

        Assert.Equal(IssueStatus.Closed, (await _issues.GetAsync(story.Id))!.Status);
        Assert.Equal(IssueStatus.Closed, (await _issues.GetAsync(epic.Id))!.Status);
    }

    [Fact]
    public async Task Story_StaysOpen_WhenTaskFailed()
    {
        var epic = await _issues.CreateAsync(new NewIssue(Type: "epic", Title: "e"));
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "test", Title: "s", Body: "b", ParentIssueId: epic.Id));
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "st", ParentId: spec.Id));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "t", ParentId: story.Id));
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "boom");

        await Tick();
        Assert.Equal(IssueStatus.Pending, (await _issues.GetAsync(story.Id))!.Status);
    }

    [Fact]
    public async Task Epic_AutoCloses_WhenTreeTerminal_StaysOpenOtherwise()
    {
        // Epic lifecycle: epics with a fully terminal tree close on
        // the assembler tick; open work anywhere keeps them open.
        var epic = await _issues.CreateAsync(new NewIssue(Type: "epic", Title: "e"));
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "test", Title: "s", Body: "b", ParentIssueId: epic.Id));
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "st", ParentId: spec.Id));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "t", ParentId: story.Id));

        // Spec not past grooming yet: epic stays open.
        await Tick();
        Assert.Equal(IssueStatus.Pending, (await _issues.GetAsync(epic.Id))!.Status);

        // Groom the spec, complete the tree: epic closes.
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved, CancellationToken.None);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Grooming, CancellationToken.None);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Groomed, CancellationToken.None);
        await _issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        await _issues.TransitionAsync(story.Id, IssueStatus.Completed, null);
        await Tick();
        Assert.Equal(IssueStatus.Closed, (await _issues.GetAsync(epic.Id))!.Status);
    }

    [Fact]
    public async Task Epic_StaysOpen_WhenDescendantFailed()
    {
        var epic = await _issues.CreateAsync(new NewIssue(Type: "epic", Title: "e"));
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "test", Title: "s", Body: "b", ParentIssueId: epic.Id));
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "st", ParentId: spec.Id));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "t", ParentId: story.Id));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved, CancellationToken.None);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Grooming, CancellationToken.None);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Groomed, CancellationToken.None);
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "boom");
        await _issues.TransitionAsync(story.Id, IssueStatus.Completed, null);

        await Tick();
        Assert.Equal(IssueStatus.Pending, (await _issues.GetAsync(epic.Id))!.Status);
    }

    [Fact]
    public async Task AssemblesActiveSprint_FromGroomedSpecGroup()
    {
        var (specId, storyIds, taskIds) = await SeedGroomedSpecAsync("Health endpoints", 3,
            epicDescription: "Ship the health/meta endpoint set.");

        await Tick();

        var active = await _sprints.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Equal("Sprint 1: Health endpoints", active.Name);
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
    public async Task BlockerTask_JoinsActiveSprint_WithoutWaitingForDrain()
    {
        // Blocker absorption (operator direction 2026-07-25): an
        // urgent groomed ad-hoc task must join the ACTIVE sprint
        // immediately — waiting for the current sprint to drain
        // burned hours + tokens when an infra fix (task-166) queued
        // behind six doomed tasks.
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 2);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;

        var blocker = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "infra blocker fix", Priority: 1,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        var normal = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "normal ad-hoc", Priority: 3,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));

        await Tick();

        var activeAfter = (await _sprints.GetActiveAsync())!;
        Assert.Equal(active.Id, activeAfter.Id);                    // sprint NOT replaced
        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.Contains(blocker.Id, members);                       // blocker absorbed
        Assert.DoesNotContain(normal.Id, members);                  // P3 ad-hoc waits for assembly
        // The in-flight tasks are untouched.
        foreach (var id in aTasks) Assert.Contains(id, members);
    }

    [Fact]
    public async Task BlockerFlag_MetadataBlockerTrue_AlsoAbsorbed()
    {
        var (_, _, _) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;

        var flagged = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "groomer-flagged blocker", Priority: 3,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["blocker"] = "true" }));

        await Tick();

        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.Contains(flagged.Id, members);
    }

    [Fact]
    public async Task UngroomedP1Task_IsNotAbsorbed()
    {
        var (_, _, _) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;

        var ungroomed = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "urgent but ungroomed", Priority: 1));

        await Tick();

        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.DoesNotContain(ungroomed.Id, members);   // grooming gate still applies
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
        Assert.Equal("Sprint 2: Sprint B", active.Name);
    }

    [Fact]
    public async Task AdHocTask_IsNeverAssembled_EvenAfterSpecSprintDrains()
    {
        // Unrelated groomed ad-hoc tasks assemble SOLO (oldest
        // first), never bundled. Related work would have injected
        // into the active sprint instead of reaching assembly.
        await SeedGroomedSpecAsync("Pipeline work", 1);
        var older = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "first one-off",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        var newer = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "second one-off",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));

        await Tick();
        var first = (await _sprints.GetActiveAsync())!;
        Assert.Equal("Sprint 1: Pipeline work", first.Name);

        foreach (var id in await _sprints.GetIssueIdsAsync(first.Id))
        {
            var issue = await _issues.GetAsync(id);
            if (issue?.Type == "task")
                await _issues.TransitionAsync(id, IssueStatus.Completed, null);
        }
        await Tick();

        // Oldest ad-hoc task gets its OWN sprint (never bundled).
        var second = (await _sprints.GetActiveAsync())!;
        Assert.Equal("Sprint 2: first one-off", second.Name);
        var members = await _sprints.GetIssueIdsAsync(second.Id);
        Assert.Contains(older.Id, members);
        Assert.DoesNotContain(newer.Id, members);

        await _issues.TransitionAsync(older.Id, IssueStatus.Completed, null);
        await Tick();
        var third = (await _sprints.GetActiveAsync())!;
        Assert.NotEqual(second.Id, third.Id);
        Assert.Equal("Sprint 3: second one-off", third.Name);
    }

    [Fact]
    public async Task AdHocFollowUp_InjectsIntoActiveSprint_UnrelatedStaysOut()
    {
        // Injection trigger 1: the task's followUpOf chain reaches a
        // sprint member — it is a continuation of the sprint's work.
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;

        var followUp = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "follow-up of sprint work",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["followUpOf"] = aTasks[0] }));
        var unrelated = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "unrelated one-off",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));

        await Tick();

        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.Contains(followUp.Id, members);
        Assert.DoesNotContain(unrelated.Id, members);
    }

    [Fact]
    public async Task AdHocUnblocker_InjectsIntoActiveSprint()
    {
        // Injection trigger 2: a blocks edge has the ad-hoc task
        // blocking a sprint member — the member cannot proceed
        // until it lands (the merge-gate harness-fix case).
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;

        var fixer = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "merge-gate fix",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        await _issues.AddDependencyAsync(fixer.Id, aTasks[0], IssueDepKind.Blocks, CancellationToken.None);

        await Tick();

        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.Contains(fixer.Id, members);
    }

    [Fact]
    public async Task RequeuedAdHoc_InjectsIntoActiveSprint()
    {
        // Injection trigger 3b: an operator requeue carries intent
        // to run — otherwise an ad-hoc task requeued from Failed
        // would strand forever (no assembly path).
        var (_, _, _) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;

        var requeued = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "operator-requeued one-off",
            Metadata: new Dictionary<string, object>
            {
                ["groomed"] = "true",
                ["requeuedFromFailedAt"] = "2026-07-27T00:00:00Z",
            }));
        var unrelated = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "unrelated one-off",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));

        await Tick();

        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.Contains(requeued.Id, members);
        Assert.DoesNotContain(unrelated.Id, members);
    }

    [Fact]
    public async Task UngroomedAdHocTask_IsNeverIngested()
    {
        // Operator rule 2026-07-23: no task enters a sprint without
        // technical grooming. Ungroomed ad-hoc: nothing happens.
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "ungroomed one-off"));
        await Tick();
        Assert.Null(await _sprints.GetActiveAsync());

        // Groomed but unrelated: assembles SOLO (own focused sprint).
        await _issues.TransitionAsync(task.Id, IssueStatus.Pending, null,
            metadata: new Dictionary<string, object> { ["groomed"] = "true" });
        await Tick();
        var active = await _sprints.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Equal("Sprint 1: ungroomed one-off", active!.Name);
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
        var storyContainer = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "container"));
        var watch = await _issues.CreateAsync(new NewIssue(Type: "pr-watch", Title: "watch"));

        await Tick();
        Assert.Null(await _sprints.GetActiveAsync()); // nothing eligible

        // Spec-chain task (assembly path; ad-hoc never assembles).
        var (specId, _, _) = await SeedGroomedSpecAsync("real work", 0);
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "real story", ParentId: specId));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "real", ParentId: story.Id));
        await Tick();
        var active = await _sprints.GetActiveAsync();
        Assert.NotNull(active);
        var members = await _sprints.GetIssueIdsAsync(active!.Id);
        Assert.DoesNotContain(epic.Id, members);
        Assert.DoesNotContain(storyContainer.Id, members);
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
        // Spec-chain variant: reassembly goes through group assembly.
        var (_, _, taskIds) = await SeedGroomedSpecAsync("requeue work", 1);
        var taskId = taskIds[0];
        await Tick();
        var first = await _sprints.GetActiveAsync();
        Assert.NotNull(first);

        // Fail the task; the sprint completes (all terminal).
        await _issues.TransitionAsync(taskId, IssueStatus.Failed, "boom");
        await Tick();
        Assert.Null(await _sprints.GetActiveAsync());

        // Operator requeue: Failed -> Pending. Next tick must assemble
        // a NEW sprint containing it (previously: stranded forever).
        await _issues.TransitionAsync(taskId, IssueStatus.Pending, "operator requeue");
        await Tick();
        var second = await _sprints.GetActiveAsync();
        Assert.NotNull(second);
        Assert.NotEqual(first!.Id, second!.Id);
        Assert.Contains(taskId, await _sprints.GetIssueIdsAsync(second.Id));
    }

    [Fact]
    public async Task EmptySprint_IsImmediatelyCompleted_AndDoesNotBlockAssembly()
    {
        // Defensive: an active sprint with no task members (e.g. only
        // stories linked) is complete by definition.
        var empty = await _sprints.CreateAsync(new NewSprint(
            Name: "empty", Goal: "g", StartDate: DateTime.UtcNow,
            EndDate: DateTime.UtcNow.AddDays(1), Status: SprintStatus.Active));

        // Spec-chain task (assembly path; ad-hoc never assembles).
        var (specId, _, _) = await SeedGroomedSpecAsync("real", 0);
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "s", ParentId: specId));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "real", ParentId: story.Id));
        await Tick();

        var all = await _sprints.ListAsync(activeOnly: false);
        Assert.Equal(SprintStatus.Completed, all.Single(s => s.Id == empty.Id).Status);
        var active = await _sprints.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Contains(task.Id, await _sprints.GetIssueIdsAsync(active!.Id));
    }
}
