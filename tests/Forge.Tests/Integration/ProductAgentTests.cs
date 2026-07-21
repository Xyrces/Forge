using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Specs;
using Forge.Tests.Integration.TestHelpers;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Phase 2c tests:
/// - ProductAgent.RefineSpecAsync with a scripted chat client that
///   calls update_spec — verifies the body gets replaced + the
///   new version is recorded with the runId author.
/// - ProductRefinementQueue wires intake.epic.accepted events to
///   ProductAgent runs.
/// - FilesystemProjectContextSource builds a context with a
///   README + a couple of .cs files for a fixture repo.
/// </summary>
public class ProductAgentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly SkillStore _skills;
    private readonly IAgentStore _agents;
    private readonly InMemoryDashboardEventBus _events;
    private readonly InMemoryProjectContextSource _projectContext;

    public ProductAgentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-product-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
        _skills = new SkillStore(_issues);
        _agents = new AgentStore(_issues);
        _events = new InMemoryDashboardEventBus();
        // Empty-project context: no repo root, so no snippets / no open issues.
        _projectContext = new InMemoryProjectContextSource(
            new ProjectContext("P", "", Array.Empty<CodeSnippet>(),
                Array.Empty<IssueRecord>(), Array.Empty<SpecRecord>(),
                Array.Empty<SkillRecord>()));
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private ProductAgent BuildAgent(IChatClient client, string? runId = "test-run")
    {
        var factory = new ScriptingChatClientFactory(client);
        var config = new LlmConfig(new ProviderConfig("test", "", null, null, "test-model"));
        return new ProductAgent(
            _specs, _issues, _projectContext, factory, config,
            new RoleAgentRegistry(), _events,
            NullLogger<ProductAgent>.Instance, runId: runId);
    }

    [Fact]
    public async Task RefineSpecAsync_AgentCallsUpdateSpec_BodyReplacedAndVersionBumped()
    {
        // Create an intake-draft spec.
        var created = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "Auth refactor", Body: "intake draft",
            ParentIssueId: "epic-1"));

        // Scripted chat client: returns a function call to update_spec
        // on the first call, then a final assistant text on the second.
        const string refined = "## Summary\nRefined body.\n\n## Acceptance criteria\n- [ ] one\n- [ ] two\n";
        var scripted = new ToolCallingChatClient(
            functionCalls: new[]
            {
                new FunctionCallContent("call_1", "update_spec",
                    new Dictionary<string, object?>
                    {
                        ["specIdArg"] = created.Id,
                        ["body"] = refined,
                        ["author"] = "product:test-run",
                    }),
            },
            followUpText: "Done refining.");

        var agent = BuildAgent(scripted, runId: "test-run");
        var refreshed = await agent.RefineSpecAsync(created.Id, "P", default);

        Assert.NotNull(refreshed);
        Assert.Equal(2, refreshed!.CurrentVersion); // v1 was intake draft, v2 is refinement
        Assert.Equal(refined, refreshed.Body);
        Assert.Equal("product:test-run", refreshed.Author);

        // The version history shows the product edit.
        var versions = await _specs.ListVersionsAsync(created.Id);
        Assert.Equal(2, versions.Count);
        Assert.Equal(1, versions[1].Version);
        Assert.Equal("intake draft", versions[1].Body);
        Assert.Equal(2, versions[0].Version);
        Assert.Equal(refined, versions[0].Body);
        Assert.Equal("product:test-run", versions[0].Author);

        // The dashboard event log has the run.completed event with
        // version=2 in metadata.
        var completed = _events.GetHistorySnapshot()
            .FirstOrDefault(e => e.Kind == "product.run.completed");
        Assert.NotNull(completed);
    }

    [Fact]
    public async Task RefineSpecAsync_AgentDoesNotCallUpdateSpec_ReturnsOriginal()
    {
        var created = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T", Body: "draft"));
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "No refinement needed.")));

        var agent = BuildAgent(scripted, runId: "test-run");
        var refreshed = await agent.RefineSpecAsync(created.Id, "P", default);

        // Spec unchanged. v1 still current.
        Assert.Equal(1, refreshed!.CurrentVersion);
        Assert.Equal("draft", refreshed.Body);
    }

    [Fact]
    public async Task RefineSpecAsync_AgentBodyTooLarge_ReturnsToolError()
    {
        var created = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T", Body: "draft"));
        var bigBody = new string('x', 300_000);
        var scripted = new ToolCallingChatClient(
            functionCalls: new[]
            {
                new FunctionCallContent("call_1", "update_spec",
                    new Dictionary<string, object?>
                    {
                        ["specIdArg"] = created.Id,
                        ["body"] = bigBody,
                        ["author"] = "product:test-run",
                    }),
            },
            followUpText: "Done.");
        var agent = BuildAgent(scripted, runId: "test-run");
        var refreshed = await agent.RefineSpecAsync(created.Id, "P", default);
        Assert.Equal(1, refreshed!.CurrentVersion); // unchanged
    }

    [Fact]
    public async Task RefineSpecAsync_UnknownSpec_ReturnsNull()
    {
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var agent = BuildAgent(scripted);
        var refreshed = await agent.RefineSpecAsync("spec-missing", "P", default);
        Assert.Null(refreshed);
    }
}

public class ProductRefinementQueueTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly IAgentStore _agents;
    private readonly SkillStore _skills;
    private readonly InMemoryDashboardEventBus _events;

    public ProductRefinementQueueTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-queue-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
        _skills = new SkillStore(_issues);
        _agents = new AgentStore(_issues);
        _events = new InMemoryDashboardEventBus();
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Queue_ReceivesAcceptedEpicEvent_RunsRefinement()
    {
        // Seed: an issue + spec with parent_issue_id.
        var issue = await _issues.CreateAsync(new NewIssue(Type: "epic", Title: "T", Description: "x"));
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T", Body: "draft",
            ParentIssueId: issue.Id));

        // Build a real factory + a scripted chat client.
        var projectContext = new InMemoryProjectContextSource(
            new ProjectContext("P", "", Array.Empty<CodeSnippet>(),
                Array.Empty<IssueRecord>(), Array.Empty<SpecRecord>(),
                Array.Empty<SkillRecord>()));
        var scripted = new ScriptedChatClient();
        scripted.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        var factory = new ScriptingChatClientFactory(scripted);

        var agentFactory = new ProductAgentFactory(
            _specs, _issues, projectContext, factory,
            new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
            new RoleAgentRegistry(), _events, null,
            NullLoggerFactory.Instance, rolePromptsRoot: "");

        await using var queue = new ProductRefinementQueue(
            agentFactory, _specs, _events,
            NullLogger<ProductRefinementQueue>.Instance);

        // Publish the accepted-epic event the way IntakeAgent does.
        _events.Publish(new DashboardEvent(
            DateTime.UtcNow, "intake.epic.accepted", "session-x", "epic accepted",
            new Dictionary<string, object?>
            {
                ["epicId"] = issue.Id,
                ["sessionId"] = "session-x",
            }));

        // Wait for the worker to process. The agent emits
        // run.started + run.completed events when it finishes.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (_events.GetHistorySnapshot().Any(e => e.Kind == "product.run.completed"))
                break;
            await Task.Delay(50);
        }

        var history = _events.GetHistorySnapshot();
        Assert.Contains(history, e => e.Kind == "product.run.started");
        Assert.Contains(history, e => e.Kind == "product.run.completed");
    }
}

public class FilesystemProjectContextSourceTests : IDisposable
{
    private readonly string _repoRoot;

    public FilesystemProjectContextSourceTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"ph-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
    }

    private static IssueStore IssueStore() => new(
        Path.Combine(Path.GetTempPath(), $"ph-context-i-{Guid.NewGuid():N}.db"));

    [Fact]
    public async Task BuildAsync_EmptyRepo_ReturnsEmptyContext()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ph-c-{Guid.NewGuid():N}.db");
        try
        {
            var issues = new IssueStore(dbPath);
            var agents = new AgentStore(issues);
            var specs = new SpecStore(issues);
            var skills = new SkillStore(issues);
            var src = new FilesystemProjectContextSource(issues, agents, specs, skills, _repoRoot);
            var ctx = await src.BuildAsync("P");
            Assert.Empty(ctx.CodeSnippets);
            Assert.Empty(ctx.OpenIssues);
            Assert.Empty(ctx.RecentSpecs);
        }
        finally { try { File.Delete(dbPath); } catch { } }
    }

    [Fact]
    public async Task BuildAsync_RepoWithReadme_IncludesReadmeSnippet()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ph-c-{Guid.NewGuid():N}.db");
        try
        {
            var issues = new IssueStore(dbPath);
            var agents = new AgentStore(issues);
            var specs = new SpecStore(issues);
            var skills = new SkillStore(issues);

            File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# Hello\n\nThis is a test repo.");
            Directory.CreateDirectory(Path.Combine(_repoRoot, "src"));
            File.WriteAllText(Path.Combine(_repoRoot, "src", "Program.cs"), "namespace X; class A {}");

            var src = new FilesystemProjectContextSource(issues, agents, specs, skills, _repoRoot);
            var ctx = await src.BuildAsync("P");
            Assert.Contains(ctx.CodeSnippets, s => s.Path == "README.md");
            Assert.Contains(ctx.CodeSnippets, s => s.Path == "Program.cs");
        }
        finally { try { File.Delete(dbPath); } catch { } }
    }
}