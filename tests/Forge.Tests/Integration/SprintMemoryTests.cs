using Forge.Agents;
using Forge.Core;
using Forge.Orchestrator;
using Forge.Orchestrator.Sprint;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Sprint flow shared memory: MemoryExtractor dual-persists under
/// `sprint/{id}/` when the issue belongs to the ACTIVE sprint, and
/// MafAgentRunner renders the sprint block (goal + roster) plus the
/// sprint-scoped memory section when the dispatch context carries
/// sprint fields.
/// </summary>
public class SprintMemoryTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;

    public SprintMemoryTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("sprint-mem");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "memory.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Extract_IssueInActiveSprint_AlsoPersistsSprintKeys()
    {
        await using var anchor = new IssueStore(_dbPath);
        var sprints = new SprintStore(anchor);
        var memories = new MemoryStore(_dbPath);
        var issue = await anchor.CreateAsync(new NewIssue(Type: "task", Title: "member task"));
        var sprint = await sprints.CreateAsync(new NewSprint(
            Name: "sp", Goal: "g", StartDate: DateTime.UtcNow,
            EndDate: DateTime.UtcNow.AddDays(1), Status: SprintStatus.Active));
        await sprints.AddIssueAsync(sprint.Id, issue.Id);

        var client = new SprintScriptedChatClient(
            "<memory><key>build/csharp</key><value>dotnet build Forge.sln is the canonical build</value></memory>");
        var extractor = new MemoryExtractor(
            new SprintScriptingFactory(client),
            new LlmConfig(new ProviderConfig("test", "", null, null, "m")),
            memories, NullLogger<MemoryExtractor>.Instance,
            sprints: sprints);

        var result = await extractor.ExtractAsync(issue.Id, "some agent output");

        Assert.Null(result.Error);
        Assert.Contains($"sprint/{sprint.Id}/build/csharp", result.PersistedKeys);
        Assert.Contains($"extraction/{issue.Id}/build/csharp", result.PersistedKeys);
        var recalled = await memories.RecallAsync($"sprint/{sprint.Id}/");
        Assert.Single(recalled);
        Assert.Equal("dotnet build Forge.sln is the canonical build", recalled[0].Body);
    }

    [Fact]
    public async Task Extract_IssueOutsideSprint_NoSprintKeys()
    {
        await using var anchor = new IssueStore(_dbPath);
        var sprints = new SprintStore(anchor);
        var memories = new MemoryStore(_dbPath);
        var issue = await anchor.CreateAsync(new NewIssue(Type: "task", Title: "outside task"));

        var client = new SprintScriptedChatClient(
            "<memory><key>k</key><value>v</value></memory>");
        var extractor = new MemoryExtractor(
            new SprintScriptingFactory(client),
            new LlmConfig(new ProviderConfig("test", "", null, null, "m")),
            memories, NullLogger<MemoryExtractor>.Instance,
            sprints: sprints);

        var result = await extractor.ExtractAsync(issue.Id, "output");

        Assert.Null(result.Error);
        Assert.DoesNotContain(result.PersistedKeys, k => k.StartsWith("sprint/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runner_SprintContext_RendersSprintBlockAndSprintMemory()
    {
        await using var anchor = new IssueStore(_dbPath);
        var memories = new MemoryStore(_dbPath);
        await memories.RememberAsync("sprint/sp-1/build-csharp", "sibling discovered: build takes ~90s", ttlDays: null);
        await memories.RememberAsync("global/fact", "global insight", ttlDays: null);

        var client = new SprintScriptedChatClient("done");
        var runner = new MafAgentRunner(
            chatClientFactory: new SprintScriptingFactory(client),
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: TempRoot.Instance.NewDirectory("sp-md"),
            memory: memories);

        await runner.RunAsync(
            AgentType.CoreDev, "do the task", sessionId: null,
            context: new Dictionary<string, object>
            {
                ["sprintId"] = "sp-1",
                ["sprintName"] = "Health endpoints",
                ["sprintGoal"] = "Ship the health/meta endpoint set.",
                ["sprintRoster"] = "- task-8 [InProgress] buildinfo endpoint",
            },
            ct: default);

        var wireText = string.Join("\n", client.CapturedMessages.Select(m => m.Text))
            + "\n" + string.Join("\n", client.CapturedInstructions);
        Assert.Contains("## Sprint", wireText);
        Assert.Contains("Ship the health/meta endpoint set.", wireText);
        Assert.Contains("task-8 [InProgress] buildinfo endpoint", wireText);
        Assert.Contains("## Sprint memory", wireText);
        Assert.Contains("sibling discovered: build takes ~90s", wireText);
        Assert.Contains("## Project memory", wireText);
        Assert.Contains("global insight", wireText);
    }

    [Fact]
    public async Task Runner_NoSprintContext_NoSprintSections()
    {
        await using var anchor = new IssueStore(_dbPath);
        var memories = new MemoryStore(_dbPath);
        await memories.RememberAsync("sprint/sp-1/x", "should not surface", ttlDays: null);

        var client = new SprintScriptedChatClient("done");
        var runner = new MafAgentRunner(
            chatClientFactory: new SprintScriptingFactory(client),
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: TempRoot.Instance.NewDirectory("sp-md"),
            memory: memories);

        await runner.RunAsync(AgentType.CoreDev, "do the task", sessionId: null, context: null, ct: default);

        var wireText = string.Join("\n", client.CapturedMessages.Select(m => m.Text))
            + "\n" + string.Join("\n", client.CapturedInstructions);
        Assert.DoesNotContain("## Sprint", wireText);
        Assert.DoesNotContain("should not surface", wireText);
    }

    /// <summary>Scripted client that also captures the request messages + instructions.</summary>
    private sealed class SprintScriptedChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _script = new();
        public List<ChatMessage> CapturedMessages { get; } = new();
        public List<string> CapturedInstructions { get; } = new();

        public SprintScriptedChatClient(params string[] replies)
        {
            foreach (var r in replies) _script.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, r)));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CapturedMessages.AddRange(messages);
            if (!string.IsNullOrEmpty(options?.Instructions)) CapturedInstructions.Add(options.Instructions);
            return Task.FromResult(_script.Count > 0
                ? _script.Dequeue()
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "default")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SprintScriptingFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public SprintScriptingFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => _client;
    }
}
