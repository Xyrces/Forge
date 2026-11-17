using Forge.Core.Initiatives;
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
        _dbPath = TempRoot.Instance.NewDbPath("product");
        _issues = new IssueStore(_dbPath,"P");
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
                        ["author"] = "model-invented-author",
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
    public async Task RefineSpecAsync_AgentDoesNotCallUpdateSpec_ReturnsNull()
    {
        var created = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "T", Body: "draft"));
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "No refinement needed.")));

        var agent = BuildAgent(scripted, runId: "test-run");
        var refreshed = await agent.RefineSpecAsync(created.Id, "P", default);

        // Spec unchanged. v1 still current.
        Assert.Null(refreshed);
        Assert.Equal("draft",(await _specs.GetAsync(created.Id))!.Body);
        Assert.DoesNotContain(_events.GetHistorySnapshot(),e=>e.Kind=="product.run.completed");
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
        Assert.Null(refreshed);
        Assert.Equal(1,(await _specs.GetAsync(created.Id))!.CurrentVersion); // unchanged
    }

    [Fact]
    public async Task RefineSpecAsync_WrongToolSpecCannotMutateAnotherProject()
    {
        var current=await _specs.CreateAsync(new("P","current","draft"));
        var other=await _specs.CreateAsync(new("other","other","untouched"));
        var client=new ToolCallingChatClient(new[] { new FunctionCallContent("wrong","update_spec",
            new Dictionary<string,object?> { ["specIdArg"]=other.Id,["body"]="bad change",["author"]="operator" }) },"done");
        Assert.Null(await BuildAgent(client).RefineSpecAsync(current.Id,"P"));
        Assert.Equal("untouched",(await _specs.GetAsync(other.Id))!.Body);
        Assert.Equal(1,(await _specs.GetAsync(current.Id))!.CurrentVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>BuildAgent(client).RefineSpecAsync(other.Id,"P"));
    }

    [Fact]
    public async Task PlainChatDoesNotCompleteDurableRefinement()
    {
        var proposals=new InitiativeProposalStore(_issues);
        var session=await new IntakeStore(_issues).CreateAsync("P","discussion");
        var proposal=await proposals.CreateAsync(new("P","source","user","scope","draft",SessionId:session.Id));
        var approved=await proposals.ApproveAsync("P",proposal.Id,proposal.Revision,proposal.Hash,"operator");
        var ingestion=new InitiativeIngestionStore(_issues);
        var service=new InitiativeApprovalService(proposals,_=>ingestion,_events);
        var client=new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant,"Done without a tool.")));
        await using var queue=new ProductRefinementQueue(service,()=>["P"],_=>ingestion,_=>_specs,
            (_,id,ct)=>BuildAgent(client).RefineSpecAsync(id,"P",ct),_events,NullLogger<ProductRefinementQueue>.Instance);
        _ = queue.RunAsync(CancellationToken.None);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        InitiativeIngestion? receipt;
        while((receipt=await ingestion.GetAsync(proposal.Id,1))?.RefinementState!="Failed") await Task.Delay(20,timeout.Token);
        Assert.Equal(SpecStatus.Draft,(await _specs.GetAsync(receipt.SpecId))!.Status);
        Assert.DoesNotContain(_events.GetHistorySnapshot(),e=>e.Kind=="product.run.completed");
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

public class FilesystemProjectContextSourceTests : IDisposable
{
    private readonly string _repoRoot;

    public FilesystemProjectContextSourceTests()
    {
        _repoRoot = TempRoot.Instance.NewDirectory("context");
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
    }

    private static IssueStore IssueStore() => new(
        TempRoot.Instance.NewDbPath("context-i"));

    [Fact]
    public async Task BuildAsync_EmptyRepo_ReturnsEmptyContext()
    {
        var dbPath = TempRoot.Instance.NewDbPath("c");
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
        var dbPath = TempRoot.Instance.NewDbPath("c");
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