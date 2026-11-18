using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Orchestrator.Composition;
using Forge.Projects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

public sealed class EngineeringArtifactScopeTests
{
    [Fact]
    public async Task RestartAfterRegisteringEarlierProjectRetainsOriginalWriterArtifacts()
    {
        var root = TempRoot.Instance.NewDirectory("artifact-controller-restart");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        await using var registryIssues = new IssueStore(ForgesystemPaths.IssuesDb(root, "default"));
        var registry = new ProjectStore(registryIssues);
        async Task Register(string id)
        {
            await registry.UpsertAsync(new NewProject(id, id, repo));
            await registry.UpdateLocalPathAsync(id, repo);
        }
        await Register("primary");
        await Register("second");
        var options = new AgentOptions
        {
            Forgesystem = new ForgesystemOptions { DataRoot = root },
            GitHub = new GitHubOptions { Owner = "fixture", Repo = "fixture", Token = "fixture" },
            Dashboard = new DashboardOptions { Enabled = false },
            Llm = new LlmOptions
            {
                DefaultProvider = "fixture",
                Providers = [new LlmProviderOptions
                {
                    Name = "fixture", BaseUrl = "http://127.0.0.1:1", ApiKey = "fixture",
                    DefaultModel = "fixture-model", Api = "openai",
                }],
            },
        };
        DesignArtifact historical;
        ArtOutput asset;
        byte[] bytes = [0x67, 0x6c, 0x54, 0x46, 0, 42, 255];
        string originalPath;
        await using (var original = await ForgeComposition.BuildAsync(options, NullLoggerFactory.Instance))
        {
            Assert.Equal("primary", original.GetRequiredService<IssueStore>().ProjectId);
            var owner = Assert.IsType<IssueStore>(original.GetRequiredService<ProjectContextFactory>().Find("second")!.Issues);
            var spec = await new SpecStore(owner).CreateAsync(new("stale-project", "Secondary spec", "body"));
            historical = await original.GetRequiredService<DesignArtifactStore>()
                .CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Original writer", "original design body", "html"));
            asset = await original.GetRequiredService<ArtOutputStore>()
                .CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "Original asset", spec.Id + "/mesh.glb", "glb"));
            originalPath = Path.Combine(ForgesystemPaths.ArtOutputDir(root, "primary"), asset.Body);
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            await File.WriteAllBytesAsync(originalPath, bytes);
            await ReadAndVerify(original);
        }

        await Register("aaa-new-primary");
        await using var restarted = await ForgeComposition.BuildAsync(options, NullLoggerFactory.Instance);
        Assert.Equal("aaa-new-primary", restarted.GetRequiredService<IssueStore>().ProjectId);
        Assert.Null(await restarted.GetRequiredService<DesignArtifactStore>().GetAsync(historical.Id));
        Assert.Null(await restarted.GetRequiredService<ArtOutputStore>().GetAsync(asset.Id));
        await ReadAndVerify(restarted);

        async Task ReadAndVerify(IServiceProvider services)
        {
            var client = new ArtifactReadClient([historical.Id, asset.Id]);
            var factory = Assert.IsType<OpenAICompatibleChatClientFactory>(services.GetRequiredService<IChatClientFactory>());
            factory.RequestPolicy = (_, _) => client;
            await services.GetRequiredService<IAgentRunner>().RunAsync(AgentType.Reviewer, "Read historical assets",
                null, new Dictionary<string, object> { ["projectId"] = "second" }, default);
            using var design = JsonDocument.Parse(client.Results[historical.Id]);
            using var art = JsonDocument.Parse(client.Results[asset.Id]);
            Assert.Equal(historical.Body, design.RootElement.GetProperty("body").GetString());
            var returned = art.RootElement.GetProperty("body").GetString()!;
            Assert.Equal(originalPath, returned);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(returned));
        }
    }

    [Fact]
    public async Task NormalInProcessCompositionReadsOwnerAndHistoryButRejectsGuessedForeignIds()
    {
        var root = TempRoot.Instance.NewDirectory("artifact-composition");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        await using var registryIssues = new IssueStore(ForgesystemPaths.IssuesDb(root, "default"));
        var registry = new ProjectStore(registryIssues);
        await registry.UpsertAsync(new NewProject("primary", "Primary", repo));
        await registry.UpdateLocalPathAsync("primary", repo);
        var options = new AgentOptions
        {
            Forgesystem = new ForgesystemOptions { DataRoot = root },
            GitHub = new GitHubOptions { Owner = "fixture", Repo = "fixture", Token = "fixture" },
            Dashboard = new DashboardOptions { Enabled = false },
            Llm = new LlmOptions
            {
                DefaultProvider = "fixture",
                Providers = [new LlmProviderOptions
                {
                    Name = "fixture", BaseUrl = "http://127.0.0.1:1", ApiKey = "fixture",
                    DefaultModel = "fixture-model", Api = "openai",
                }],
            },
        };
        await using var services = await ForgeComposition.BuildAsync(options, NullLoggerFactory.Instance);
        var projects = services.GetRequiredService<ProjectContextFactory>();
        // Register after normal composition, exercising its live project lookup.
        await registry.UpsertAsync(new NewProject("second", "Second", repo));
        await registry.UpdateLocalPathAsync("second", repo);
        var owner = Assert.IsType<IssueStore>(projects.Find("second")!.Issues);
        var ownerSpecs = new SpecStore(owner);
        var spec = await ownerSpecs.CreateAsync(new NewSpec("second", "Owned", "owner spec body"));
        var stale = await ownerSpecs.CreateAsync(new NewSpec("stale-column", "Physical owner", "physical owner body"));
        var primarySpecs = services.GetRequiredService<SpecStore>();
        var foreignSpec = await primarySpecs.CreateAsync(new NewSpec("primary", "Foreign", "foreign spec body"));
        var primaryDesigns = services.GetRequiredService<DesignArtifactStore>();
        var primaryArt = services.GetRequiredService<ArtOutputStore>();
        var ownerDesigns = new DesignArtifactStore(owner.Db);
        var ownerArt = new ArtOutputStore(owner.Db);
        var design = await ownerDesigns.CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Owned", "owner design body", "html"));
        var legacy = await primaryDesigns.CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Historical", "historical design body", "html"));
        var foreign = await primaryDesigns.CreateAsync(new(foreignSpec.Id, DesignArtifactKind.Wireframe, "Foreign", "foreign design body", "html"));
        var misplaced = await ownerDesigns.CreateAsync(new(foreignSpec.Id, DesignArtifactKind.Wireframe, "Misplaced", "misplaced foreign body", "html"));
        var orphan = await primaryDesigns.CreateAsync(new("spec-unknown", DesignArtifactKind.Wireframe, "Orphan", "orphan body", "html"));
        var art = await ownerArt.CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "Owned", spec.Id + "/owned.glb", "glb"));
        var legacyArt = await primaryArt.CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "Historical", spec.Id + "/historical.glb", "glb"));
        var foreignArt = await primaryArt.CreateAsync(new(foreignSpec.Id, ArtOutputKind.Mesh, "Foreign", "foreign/model.glb", "glb"));
        var currentPath = Path.Combine(ForgesystemPaths.ArtOutputDir(root, "second"), art.Body);
        var historicalPath = Path.Combine(ForgesystemPaths.ArtOutputDir(root, "primary"), legacyArt.Body);
        foreach (var path in new[] { currentPath, historicalPath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "artifact bytes");
        }
        var expected = new Dictionary<string, string?>
        {
            [spec.Id] = spec.Body, [stale.Id] = stale.Body,
            [design.Id] = design.Body, [legacy.Id] = legacy.Body,
            [art.Id] = currentPath, [legacyArt.Id] = historicalPath,
            [foreignSpec.Id] = null, [foreign.Id] = null, [foreignArt.Id] = null,
            [misplaced.Id] = null, [orphan.Id] = null,
        };
        var client = new ArtifactReadClient(expected.Keys.ToArray());
        // Keep the real composition/runner/tool construction. Replace only the
        // provider transport through its existing request-policy seam: no HTTP.
        var factory = Assert.IsType<OpenAICompatibleChatClientFactory>(services.GetRequiredService<IChatClientFactory>());
        factory.RequestPolicy = (_, _) => client;
        var runner = Assert.IsType<MafAgentRunner>(services.GetRequiredService<IAgentRunner>());
        await runner.RunAsync(AgentType.Reviewer, "Read the referenced artifacts", null,
            new Dictionary<string, object> { ["projectId"] = "second" }, CancellationToken.None);
        Assert.Equal(expected.Count, client.Results.Count);
        foreach (var (id, body) in expected)
        {
            using var result = JsonDocument.Parse(client.Results[id]);
            if (body is null) Assert.Equal("not_found", result.RootElement.GetProperty("error").GetString());
            else Assert.Equal(body, result.RootElement.GetProperty("body").GetString());
        }
        var reads = await new ContextHandoffStore(owner.Db).ListForTaskAsync("");
        Assert.Equal(expected.Count(pair => pair.Value is not null), reads.Count);
        Assert.Empty(await new ContextHandoffStore(services.GetRequiredService<IssueStore>().Db).ListForTaskAsync(""));

        var primaryClient = new ArtifactReadClient([foreignSpec.Id, foreign.Id, legacy.Id]);
        factory.RequestPolicy = (_, _) => primaryClient;
        await runner.RunAsync(AgentType.Reviewer, "Read primary project artifacts", null,
            new Dictionary<string, object> { ["projectId"] = "primary" }, CancellationToken.None);
        Assert.Contains("foreign spec body", primaryClient.Results[foreignSpec.Id]);
        Assert.Contains("foreign design body", primaryClient.Results[foreign.Id]);
        Assert.Contains("not_found", primaryClient.Results[legacy.Id]);

        foreach (var projectId in new string?[] { "unknown-project", null })
        {
            var noScope = new ArtifactReadClient([]);
            factory.RequestPolicy = (_, _) => noScope;
            var context = projectId is null ? null : new Dictionary<string, object> { ["projectId"] = projectId };
            await runner.RunAsync(AgentType.Reviewer, "No unscoped artifact access", null, context, CancellationToken.None);
            Assert.False(noScope.ArtifactToolOffered);
        }
    }

    [Fact]
    public async Task WorkerLocalConstructionPreservesArtPathsAndRejectsForeignSpecReferences()
    {
        var root = TempRoot.Instance.NewDirectory("artifact-worker");
        await using var issues = new IssueStore(Path.Combine(root, "issues.db"), "second");
        var specs = new SpecStore(issues);
        var spec = await specs.CreateAsync(new NewSpec("stale-project-text", "Owned", "owned worker spec"));
        var designs = new DesignArtifactStore(issues.Db);
        var art = new ArtOutputStore(issues.Db);
        var output = await art.CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "Owned", "assets/mesh.glb", "glb"));
        var foreign = await designs.CreateAsync(new("spec-foreign", DesignArtifactKind.Wireframe, "Foreign", "foreign body", "html"));
        var client = new ArtifactReadClient([spec.Id, output.Id, foreign.Id]);
        var runner = new MafAgentRunner(new ClientFactory(client),
            new LlmConfig(new ProviderConfig("fixture", "", null, null, "fixture-model")),
            new RoleAgentRegistry(), NullLogger<MafAgentRunner>.Instance,
            rolePromptsRoot: root, issues: issues, designArtifacts: () => designs,
            specs: () => specs, artOutputs: () => art);
        await runner.RunAsync(AgentType.Reviewer, "Read worker artifacts", null,
            new Dictionary<string, object> { ["projectId"] = "second", ["workerAttemptId"] = "fixture" },
            CancellationToken.None);
        Assert.Contains("owned worker spec", client.Results[spec.Id]);
        Assert.Contains("assets/mesh.glb", client.Results[output.Id]);
        Assert.Contains("not_found", client.Results[foreign.Id]);
    }

    private sealed class ClientFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null,
            RoleModel? modelOverride = null) => client;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("single")]
    public async Task ConcreteSingleProjectFactoriesRemainUsableWithoutOptionalIssueStore(string? projectId)
    {
        var root = TempRoot.Instance.NewDirectory("artifact-single");
        await using var issues = new IssueStore(Path.Combine(root, "issues.db"), "single");
        var specs = new SpecStore(issues);
        var spec = await specs.CreateAsync(new NewSpec("single", "Owned", "single project spec"));
        var designs = new DesignArtifactStore(issues.Db);
        var design = await designs.CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Owned", "single design body", "html"));
        var foreign = await designs.CreateAsync(new("spec-foreign", DesignArtifactKind.Wireframe, "Foreign", "foreign body", "html"));
        var client = new ArtifactReadClient([spec.Id, design.Id, foreign.Id]);
        var runner = new MafAgentRunner(new ClientFactory(client),
            new LlmConfig(new ProviderConfig("fixture", "", null, null, "fixture-model")),
            new RoleAgentRegistry(), NullLogger<MafAgentRunner>.Instance, rolePromptsRoot: root,
            designArtifacts: () => designs, specs: () => specs, artOutputs: () => new ArtOutputStore(issues.Db));
        var context = projectId is null ? null : new Dictionary<string, object> { ["projectId"] = projectId };
        await runner.RunAsync(AgentType.Reviewer, "Read single-project artifacts", null, context, CancellationToken.None);
        Assert.True(client.ArtifactToolOffered);
        Assert.Contains("single project spec", client.Results[spec.Id]);
        Assert.Contains("single design body", client.Results[design.Id]);
        Assert.Contains("not_found", client.Results[foreign.Id]);
    }

}

internal sealed class ArtifactReadClient(string[] ids) : IChatClient
{
    private bool _called;
    public bool ArtifactToolOffered { get; private set; }
    public Dictionary<string, string> Results { get; } = [];
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!_called)
        {
            _called = true;
            ArtifactToolOffered = options?.Tools?.OfType<AIFunction>().Any(t => t.Name == "read_artifact") == true;
            if (ids.Length > 0)
            {
                Assert.True(ArtifactToolOffered);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    ids.Select((id, index) => (AIContent)new FunctionCallContent(index.ToString(), "read_artifact",
                        new Dictionary<string, object?> { ["artifactId"] = id })).ToList())));
            }
        }
        foreach (var result in messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
            Results[ids[int.Parse(result.CallId)]] = result.Result?.ToString() ?? "null";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Artifact reads complete.")));
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
