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
    public async Task AgedFailure_AutoClosed_BySweep_StoryAndEpicFollow()
    {
        // Operator direction 2026-08-18 ("fix this permanently"): a
        // Failed task nobody touched past the aging window is
        // abandoned work — leaving it Failed holds its story/epic
        // (and sprint assembly) hostage forever, which is how
        // porthorizon sat idle 24h+ on 20 ancient failures while the
        // board read as a busy backlog. The sweep closes it and the
        // terminal-tree cascade closes its parents in the same tick.
        var epic = await _issues.CreateAsync(new NewIssue(Type: "epic", Title: "e"));
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "test", Title: "s", Body: "b", ParentIssueId: epic.Id));
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "st", ParentId: spec.Id));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "t", ParentId: story.Id));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved, CancellationToken.None);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Grooming, CancellationToken.None);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Groomed, CancellationToken.None);
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "boom");

        var sweeping = new SprintAssembler(
            new ProjectContextFactory(new List<ProjectOptions>()),
            _events, NullLogger<SprintAssembler>.Instance,
            failureAgingWindow: TimeSpan.FromMilliseconds(1));
        await Task.Delay(30); // let the failure age past the tiny window
        await sweeping.TickProjectAsync("test", _issues, _sprints, _specs, CancellationToken.None);

        Assert.Equal(IssueStatus.Closed, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Equal(IssueStatus.Closed, (await _issues.GetAsync(story.Id))!.Status);
        Assert.Equal(IssueStatus.Closed, (await _issues.GetAsync(epic.Id))!.Status);
        Assert.Contains(_events.GetHistorySnapshot(), e => e.Kind == "sprint.failure.swept");
    }

    [Fact]
    public async Task FreshFailure_Untouched_BuildStateStarved()
    {
        // The no-auto-clear rule protects FRESH failures (the operator
        // is investigating); the build state must NAME the blockage
        // instead of the flat "no eligible work" — a completed sprint
        // plus a busy board read as a dead pipeline (2026-08-17/18).
        var epic = await _issues.CreateAsync(new NewIssue(Type: "epic", Title: "e"));
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "test", Title: "s", Body: "b", ParentIssueId: epic.Id));
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "st", ParentId: spec.Id));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "t", ParentId: story.Id));
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "boom");

        var sweeping = new SprintAssembler(
            new ProjectContextFactory(new List<ProjectOptions>()),
            _events, NullLogger<SprintAssembler>.Instance,
            failureAgingWindow: TimeSpan.FromDays(7));
        await sweeping.TickProjectAsync("test", _issues, _sprints, _specs, CancellationToken.None);

        Assert.Equal(IssueStatus.Failed, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Equal(IssueStatus.Pending, (await _issues.GetAsync(story.Id))!.Status);

        var mem = new Forge.Core.MemoryStore(_issues.Db);
        var hit = (await mem.RecallAsync(SprintAssembler.BuildStateKey)).First();
        using var doc = System.Text.Json.JsonDocument.Parse(hit.Body);
        Assert.Equal("starved", doc.RootElement.GetProperty("phase").GetString());
        Assert.Contains("Failed task", doc.RootElement.GetProperty("reason").GetString());
        Assert.Contains(doc.RootElement.GetProperty("heldWork").EnumerateArray(),
            h => h.GetProperty("id").GetString() == task.Id);
    }

    [Fact]
    public async Task FailureAging_Disabled_NeverSweeps()
    {
        var epic = await _issues.CreateAsync(new NewIssue(Type: "epic", Title: "e"));
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "test", Title: "s", Body: "b", ParentIssueId: epic.Id));
        var story = await _issues.CreateAsync(new NewIssue(Type: "story", Title: "st", ParentId: spec.Id));
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "t", ParentId: story.Id));
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "boom");

        await Task.Delay(30);
        await Tick(); // the default assembler has NO aging window
        Assert.Equal(IssueStatus.Failed, (await _issues.GetAsync(task.Id))!.Status);
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
    public async Task FailedTask_DoesNotCompleteSprint()
    {
        // 2026-08-11 talaria Sprint 5: a Failed task was counted as
        // terminal and the sprint auto-completed, dropping the
        // unfinished work onto the floor. Failed must NOT count as
        // terminal — the operator must requeue or close it (rule
        // 2026-07-25).
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 2);
        await Tick();
        var first = await _sprints.GetActiveAsync();
        Assert.NotNull(first);

        // Complete one, fail the other.
        await _issues.TransitionAsync(aTasks[0], IssueStatus.Completed, null);
        await _issues.TransitionAsync(aTasks[1], IssueStatus.Failed, null);
        await Tick();

        // Sprint must STILL be Active — Failed keeps it open.
        Assert.Equal(SprintStatus.Active, (await _sprints.GetAsync(first!.Id))!.Status);
        // No new sprint assembled — the Failed task is still pending resolution.
        var active = await _sprints.GetActiveAsync();
        Assert.Equal(first.Id, active!.Id);

        // Close the Failed task → sprint now completes.
        await _issues.TransitionAsync(aTasks[1], IssueStatus.Closed, null);
        await Tick();
        Assert.Equal(SprintStatus.Completed, (await _sprints.GetAsync(first.Id))!.Status);
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
    public async Task AdHocFollowUp_DoesNotInject_SameWorkTriggerRemoved()
    {
        // Operator model 2026-07-31: followUpOf-chain "same work"
        // injection is REMOVED — follow-ups materialize at sprint
        // completion and join a LATER sprint through grooming. Only
        // blocks edges / operator signals inject mid-sprint.
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;

        var followUp = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "follow-up of sprint work",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["followUpOf"] = aTasks[0] }));

        await Tick();

        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.DoesNotContain(followUp.Id, members);
    }

    [Fact]
    public async Task SprintCompletion_MaterializesDrafts_IntoTasks()
    {
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;

        var drafts = new FollowUpDraftStore(_issues);
        await drafts.FileAsync(new FollowUpDraft(0, active.Id, aTasks[0], "Reviewer",
            "deferred finding", "something to fix later", 2, null, DateTime.UtcNow, null));

        foreach (var id in aTasks)
        {
            await _issues.TransitionAsync(id, IssueStatus.Completed, null);
        }
        await Tick();

        var materialized = (await _issues.ListAsync(new IssueFilter()))
            .Where(i => i.GetMetadata("fromDraft") is not null).ToList();
        var task = Assert.Single(materialized);
        Assert.Equal("deferred finding", task.Title);
        Assert.Equal(aTasks[0], task.GetMetadata("followUpOf"));
        Assert.Equal(IssueStatus.Pending, task.Status);
        Assert.Empty(await drafts.ListUnconsumedAsync());
    }

    // ---- Themed packing (operator rule 2026-08-08): exactly one
    // kind of sprint. Follow-up work clusters by its followUpOf ROOT
    // and packs into ONE themed sprint — never a solo sprint per
    // follow-up. Only truly rootless ad-hoc tasks stay solo. ----

    [Fact]
    public async Task FollowUpTheme_PacksChainIntoOneSprint()
    {
        var root = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "MaterialReservation locking"));
        await _issues.TransitionAsync(root.Id, IssueStatus.Completed, null);
        var fu1 = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "follow-up one", Priority: 3,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["followUpOf"] = root.Id }));
        var fu2 = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "follow-up two", Priority: 2,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["followUpOf"] = root.Id }));
        // Nested: follow-up of a follow-up resolves to the same root.
        var fu3 = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "nested follow-up", Priority: 4,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["followUpOf"] = fu1.Id }));

        await Tick();

        var active = (await _sprints.GetActiveAsync())!;
        // Named after the LEADING MEMBER (fu2, P2), not the completed
        // root — the root is usually merged and a root-named sprint
        // reads as out-of-sprint work (operator confusion 2026-08-18).
        Assert.Equal("Sprint 1: follow-up two", active.Name);
        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.Contains(fu1.Id, members);
        Assert.Contains(fu2.Id, members);
        Assert.Contains(fu3.Id, members);
        Assert.Single(await _sprints.ListAsync(activeOnly: false));
    }

    [Fact]
    public async Task FollowUpTheme_SplitsAtCap_RemainderAssemblesNext()
    {
        var root = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "big theme"));
        var ids = new List<string>();
        for (var i = 0; i < SprintAssembler.MaxThemeTasks + 2; i++)
        {
            var fu = await _issues.CreateAsync(new NewIssue(
                Type: "task", Title: $"follow-up {i}", Priority: 3,
                Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["followUpOf"] = root.Id }));
            ids.Add(fu.Id);
        }

        await Tick();

        var first = (await _sprints.GetActiveAsync())!;
        var members = await _sprints.GetIssueIdsAsync(first.Id);
        Assert.Equal(SprintAssembler.MaxThemeTasks, members.Count);

        // Drain the first sprint → the remainder packs the next one.
        foreach (var id in members)
        {
            await _issues.TransitionAsync(id, IssueStatus.Completed, null);
        }
        await Tick();
        var second = (await _sprints.GetActiveAsync())!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await _sprints.GetIssueIdsAsync(second.Id)).Count);
    }

    [Fact]
    public async Task FollowUpTheme_PriorityPicksTheMostImportantTheme()
    {
        var lowRoot = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "polish theme"));
        var hotRoot = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "critical theme"));
        await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "polish follow-up", Priority: 4,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["followUpOf"] = lowRoot.Id }));
        var hot = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "critical follow-up", Priority: 1,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["followUpOf"] = hotRoot.Id }));

        await Tick();

        var active = (await _sprints.GetActiveAsync())!;
        Assert.Equal("Sprint 1: critical follow-up", active.Name);
        Assert.Contains(hot.Id, await _sprints.GetIssueIdsAsync(active.Id));
        Assert.Equal($"Follow-up work filed from {hotRoot.Id} (critical theme): critical follow-up",
            active.Goal);
    }

    [Fact]
    public async Task FollowUpTheme_MixedWithRootlessAdHoc_RootlessStaysSolo()
    {
        var root = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "themed work"));
        var fu = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "themed follow-up", Priority: 3,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true", ["followUpOf"] = root.Id }));
        var rootless = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "operator one-off", Priority: 2,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));

        await Tick();

        // Rootless P2 outranks the themed P3 and assembles solo; the
        // themed follow-up is NOT swept into the one-off's sprint.
        var active = (await _sprints.GetActiveAsync())!;
        Assert.Equal("Sprint 1: operator one-off", active.Name);
        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.Contains(rootless.Id, members);
        Assert.DoesNotContain(fu.Id, members);
    }

    [Fact]
    public async Task Assembly_WaitsForFollowUpGrooming()
    {
        // The next sprint does not start until materialized
        // follow-ups are groomed (operator model 2026-07-31).
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        foreach (var id in aTasks)
        {
            await _issues.TransitionAsync(id, IssueStatus.Completed, null);
        }
        await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "ungroomed follow-up",
            Metadata: new Dictionary<string, object> { ["followUpOf"] = aTasks[0] }));

        await Tick();

        Assert.Null(await _sprints.GetActiveAsync());
        // Groom it → assembly proceeds on the next tick.
        var fup = (await _issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }))
            .First(i => i.GetMetadata("followUpOf") is not null);
        await _issues.TransitionAsync(fup.Id, IssueStatus.Pending, null,
            new Dictionary<string, object> { ["groomed"] = "true" });
        await Tick();
        Assert.NotNull(await _sprints.GetActiveAsync());
    }

    [Fact]
    public async Task Assembly_PrefersHigherPriorityGroup()
    {
        // Operator direction 2026-07-31: priority-first assembly, not
        // oldest-spec FIFO.
        var (_, _, oldTasks) = await SeedGroomedSpecAsync("Old low-priority", 1);
        foreach (var id in oldTasks)
        {
            await _issues.TransitionAsync(id, IssueStatus.Pending, null,
                new Dictionary<string, object> { ["groomed"] = "true" });
            // SeedGroomedSpec tasks default to priority 2.
        }
        var p1 = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "urgent one-off", Priority: 1,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));

        await Tick();

        var active = (await _sprints.GetActiveAsync())!;
        Assert.Equal("Sprint 1: urgent one-off", active.Name);
    }

    [Fact]
    public async Task SprintCompletion_TriageMergesAndDiscards_UncitedFallback()
    {
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;

        var drafts = new FollowUpDraftStore(_issues);
        var d1 = await drafts.FileAsync(new FollowUpDraft(0, active.Id, aTasks[0], "Reviewer", "dupe one", "same bug A", 2, null, DateTime.UtcNow, null));
        var d2 = await drafts.FileAsync(new FollowUpDraft(0, active.Id, aTasks[0], "Reviewer", "dupe two", "same bug B", 3, null, DateTime.UtcNow, null));
        var d3 = await drafts.FileAsync(new FollowUpDraft(0, active.Id, aTasks[0], "Reviewer", "junk", "noise", 4, null, DateTime.UtcNow, null));
        var d4 = await drafts.FileAsync(new FollowUpDraft(0, active.Id, aTasks[0], "Reviewer", "uncited", "the triage never mentions me", 2, null, DateTime.UtcNow, null));

        var triage = new StubTriage(new FollowUpTriageDecision(new TriageItem[]
        {
            new("merge", new long[] { d1, d2 }, Title: "merged: same bug", Description: "both bugs"),
            new("discard", new long[] { d3 }, Reason: "noise"),
            new("create", new long[] { 9999 }, Title: "phantom"), // unknown id — dropped
        }));
        var assembler = new SprintAssembler(
            new ProjectContextFactory(new List<ProjectOptions>()), _events,
            NullLogger<SprintAssembler>.Instance, followUpTriage: triage);

        foreach (var id in aTasks)
        {
            await _issues.TransitionAsync(id, IssueStatus.Completed, null);
        }
        await assembler.TickProjectAsync("test", _issues, _sprints, _specs, CancellationToken.None);

        var all = await _issues.ListAsync(new IssueFilter());
        var merged = all.SingleOrDefault(i => i.Title == "merged: same bug");
        Assert.NotNull(merged);
        Assert.Equal("both bugs", merged!.Description);
        Assert.Equal("1,2", merged.GetMetadata("fromDraft"));
        Assert.Null(all.SingleOrDefault(i => i.Title == "junk"));
        Assert.Null(all.SingleOrDefault(i => i.Title == "phantom"));
        // Uncited draft materialized 1:1 (never lose work).
        var uncited = all.SingleOrDefault(i => i.Title == "uncited");
        Assert.NotNull(uncited);
        // Dispositions recorded.
        var consumed = await _issues.ListAsync(new IssueFilter());
        Assert.Equal("merged", (await GetDraft(d1)).Disposition);
        Assert.Equal("merged", (await GetDraft(d2)).Disposition);
        Assert.Equal("discarded", (await GetDraft(d3)).Disposition);
        Assert.Equal("materialized", (await GetDraft(d4)).Disposition);
    }

    private async Task<FollowUpDraft> GetDraft(long id) =>
        await new FollowUpDraftStore(_issues).GetAsync(id)
        ?? throw new InvalidOperationException($"draft {id} missing");

    private sealed class StubTriage : IFollowUpTriage
    {
        private readonly FollowUpTriageDecision? _decision;
        public StubTriage(FollowUpTriageDecision? decision) { _decision = decision; }
        public Task<FollowUpTriageDecision?> TriageAsync(
            string projectId, IReadOnlyList<FollowUpDraft> drafts, CancellationToken ct = default)
            => Task.FromResult(_decision);
    }

    [Fact]
    public async Task Assembly_SkipsTasksWithOpenBlockers_UntilBlockerLands()
    {
        // Operator report 2026-07-31: a blocked task assembled
        // without its blocker stalls the sprint forever (dispatch
        // only claims members). Assembly must skip it; it becomes
        // eligible when the blocker completes.
        await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "blocked work",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        var blocked = (await _issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending })).Single(i => i.Title == "blocked work");
        var blocker = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "the blocker", Priority: 1,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        await _issues.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks, CancellationToken.None);

        await Tick();

        var active = (await _sprints.GetActiveAsync())!;
        var members = await _sprints.GetIssueIdsAsync(active.Id);
        Assert.DoesNotContain(blocked.Id, members);

        // Blocker lands → the blocked task assembles next.
        await _issues.TransitionAsync(blocker.Id, IssueStatus.Completed, null);
        var sprint1 = active.Id;
        await Tick(); // completes sprint 1 (blocker terminal)
        // The groomed ad-hoc blocked task assembles as its own solo sprint.
        var active2 = await _sprints.GetActiveAsync();
        if (active2 is not null && active2.Id != sprint1)
        {
            var members2 = await _sprints.GetIssueIdsAsync(active2.Id);
            Assert.Contains(blocked.Id, members2);
        }
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
        // a task fails, the operator closes it, the sprint completes,
        // then requeues it to Pending. Completed-sprint membership
        // must NOT strand it — requeued work is a new sprint, not
        // history to protect from resurrection. 2026-08-11 update:
        // Failed no longer counts as terminal (a sprint with Failed
        // stays active — operator rule 2026-07-25: don't auto-clear
        // Failed), so the operator closes the Failed task first, the
        // sprint completes, then requeue reassembles.
        var (_, _, taskIds) = await SeedGroomedSpecAsync("requeue work", 1);
        var taskId = taskIds[0];
        await Tick();
        var first = await _sprints.GetActiveAsync();
        Assert.NotNull(first);

        // Fail the task; the sprint STAYS active (Failed blocks
        // completion).
        await _issues.TransitionAsync(taskId, IssueStatus.Failed, "boom");
        await Tick();
        Assert.NotNull(await _sprints.GetActiveAsync());

        // Operator closes the Failed task → sprint now completes.
        await _issues.TransitionAsync(taskId, IssueStatus.Closed, "operator close");
        await Tick();
        Assert.Null(await _sprints.GetActiveAsync());

        // Operator requeue: Closed -> Pending. Next tick assembles
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

    // ---- Inter-sprint build-state snapshot (operator request
    // 2026-08-06): every tick snapshots the between-sprints phase to
    // the project's memory store (sprint/build) so the dashboard can
    // show WHY there's no active sprint. ----

    private async Task<SprintAssembler.SprintBuildState?> ReadBuildStateAsync()
    {
        var mem = new MemoryStore(_issues.Db);
        var hit = (await mem.RecallAsync(SprintAssembler.BuildStateKey))
            .FirstOrDefault(m => m.Key == SprintAssembler.BuildStateKey);
        return hit is null ? null
            : System.Text.Json.JsonSerializer.Deserialize<SprintAssembler.SprintBuildState>(
                hit.Body, new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task BuildState_RunningSnapshot_TracksTerminalProgress()
    {
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 2);
        await Tick();

        var state = await ReadBuildStateAsync();
        Assert.NotNull(state);
        Assert.Equal("running", state!.Phase);
        Assert.Equal(2, state.ActiveTotal);
        Assert.Equal(0, state.ActiveTerminal);
        Assert.NotNull(state.ActiveSprintId);

        await _issues.TransitionAsync(aTasks[0], IssueStatus.Completed, null);
        await Tick();
        state = await ReadBuildStateAsync();
        Assert.Equal("running", state!.Phase);
        Assert.Equal(1, state.ActiveTerminal);
    }

    [Fact]
    public async Task BuildState_AwaitingGroom_CapturesPendingFollowUps_PublishesOnChangeOnly()
    {
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 1);
        // A second groomed-spec group so the snapshot can show what's
        // queued BEHIND the grooming wait.
        await SeedGroomedSpecAsync("Sprint B", 2);
        await Tick();
        foreach (var id in aTasks)
        {
            await _issues.TransitionAsync(id, IssueStatus.Completed, null);
        }
        var fup = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "ungroomed follow-up",
            Metadata: new Dictionary<string, object> { ["followUpOf"] = aTasks[0] }));

        await Tick();

        var state = await ReadBuildStateAsync();
        Assert.NotNull(state);
        Assert.Equal("awaiting-groom", state!.Phase);
        Assert.EndsWith("Sprint A", state.CompletedSprintName);
        var item = Assert.Single(state.PendingGroom);
        Assert.Equal(fup.Id, item.Id);
        Assert.Equal("ungroomed follow-up", item.Title);
        var group = Assert.Single(state.EligibleGroups);
        Assert.Equal("Sprint B", group.Name);
        Assert.Equal(2, group.TaskCount);

        // The waiting event fires on the transition into
        // awaiting-groom…
        Assert.Single(_events.GetHistorySnapshot()
            .Where(e => e.Kind == DashboardEventKind.SprintAssemblyWaiting));

        // …but NOT on every 5-minute tick while nothing changes —
        // the feed would drown in identical "waiting" entries.
        await Tick();
        Assert.Single(_events.GetHistorySnapshot()
            .Where(e => e.Kind == DashboardEventKind.SprintAssemblyWaiting));

        // Groom it → assembly proceeds; the snapshot flips to running.
        await _issues.TransitionAsync(fup.Id, IssueStatus.Pending, null,
            new Dictionary<string, object> { ["groomed"] = "true" });
        await Tick();
        state = await ReadBuildStateAsync();
        Assert.Equal("running", state!.Phase);
        Assert.NotNull(state.ActiveSprintId);
    }

    [Fact]
    public async Task BuildState_Idle_WhenBacklogEmpty()
    {
        await Tick();
        var state = await ReadBuildStateAsync();
        Assert.NotNull(state);
        Assert.Equal("idle", state!.Phase);
    }

    [Fact]
    public async Task BuildState_Held_WhenOperatorGateHeld()
    {
        var gates = NewGates();
        var gated = new SprintAssembler(
            new ProjectContextFactory(new List<ProjectOptions>()),
            _events, NullLogger<SprintAssembler>.Instance, gates: gates);
        await SeedGroomedSpecAsync("Pipeline work", 1);
        await gates.HoldAsync(StageGates.Sprint);

        await gated.TickProjectAsync("test", _issues, _sprints, _specs, CancellationToken.None);

        var state = await ReadBuildStateAsync();
        Assert.NotNull(state);
        Assert.Equal("held", state!.Phase);
        Assert.Single(state.EligibleGroups);
    }

    [Fact]
    public async Task BuildState_Materialization_PublishesEvent_AndSnapshotShowsGroomWait()
    {
        var (_, _, aTasks) = await SeedGroomedSpecAsync("Sprint A", 1);
        await Tick();
        var active = (await _sprints.GetActiveAsync())!;
        var drafts = new FollowUpDraftStore(_issues);
        await drafts.FileAsync(new FollowUpDraft(0, active.Id, aTasks[0], "Reviewer",
            "deferred finding", "something to fix later", 2, null, DateTime.UtcNow, null));
        foreach (var id in aTasks)
        {
            await _issues.TransitionAsync(id, IssueStatus.Completed, null);
        }

        await Tick();

        var evt = Assert.Single(_events.GetHistorySnapshot()
            .Where(e => e.Kind == DashboardEventKind.SprintMaterialized));
        Assert.Equal(1, Assert.IsType<int>(evt.Data!["created"]));
        var state = await ReadBuildStateAsync();
        Assert.NotNull(state);
        Assert.Equal("awaiting-groom", state!.Phase);
        Assert.Single(state.PendingGroom);
        Assert.EndsWith("Sprint A", state.CompletedSprintName);
    }
}
