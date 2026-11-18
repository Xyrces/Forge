using System.Reflection;
using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Execution;
using Forge.Orchestrator.Workers;
using Forge.Tests.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class WorkerHistoricalArtifactTests
{
    [Fact]
    public async Task DispatcherPinsHistoricalProjectAndAdoptionPreservesOriginalAssignment()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("history-dispatch"));
        var attempts = new WorkerAttemptStore(issues.Db);
        var jobs = new RecordingJobs(attempts);
        var context = new Dictionary<string, object>
        {
            ["projectId"] = "second", ["issueId"] = "task-1", ["dispatchId"] = "dispatch-1",
            ["worktreePath"] = TempRoot.Instance.NewDirectory("history-dispatch-tree"),
            ["sourceSha"] = new string('a', 40), ["historicalProjectId"] = "injected-project",
        };
        var original = new KubernetesAgentRunner(attempts, jobs, "dataset", historicalProjectId: "primary");
        await Assert.ThrowsAsync<WorkerExecutionUncertainException>(() =>
            original.RunAsync(AgentType.CoreDev, "task", null, context, default));
        var queued = Assert.Single(jobs.Created);
        var invocation = JsonSerializer.Deserialize<WorkerInvocation>(queued.AssignmentJson)!;
        Assert.Equal("primary", invocation.HistoricalProjectId);
        Assert.Equal("injected-project", invocation.Context["historicalProjectId"]);
        jobs.FailCreation = false;
        var replacement = new KubernetesAgentRunner(attempts, jobs, "dataset", historicalProjectId: "aaa-added-later");
        await replacement.RunAsync(AgentType.CoreDev, "task", null, context, default);
        Assert.Equal(2, jobs.Created.Count);
        Assert.Equal(queued.Id, jobs.Created[1].Id);
        Assert.Equal(queued.AssignmentJson, jobs.Created[1].AssignmentJson);
        Assert.Equal("primary", JsonSerializer.Deserialize<WorkerInvocation>(jobs.Created[1].AssignmentJson)!.HistoricalProjectId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerReadsOwnedHistoricalAndCurrentArtifactsAndRejectsOtherProjects(bool legacyAssignment)
    {
        await using var fixture = await Fixture.Create();
        if (!legacyAssignment) await fixture.Register("aaa-added-later");
        var ownerSpecs = new SpecStore(fixture.Secondary);
        var spec = await ownerSpecs.CreateAsync(new NewSpec("stale-project-column", "Owned", "owned spec"));
        var foreign = await new SpecStore(fixture.Primary).CreateAsync(new NewSpec("primary", "Foreign", "foreign spec"));
        var primaryDesigns = new DesignArtifactStore(fixture.Primary.Db);
        var localDesigns = new DesignArtifactStore(fixture.Secondary.Db);
        var local = await localDesigns.CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Current", "current design", "html"));
        var history = await primaryDesigns.CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Historical", "historical design", "html"));
        var duplicate = await primaryDesigns.CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Old copy", "obsolete historical copy", "html"));
        await using (var connection = await fixture.Primary.Db.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE design_artifact SET id=@current WHERE id=@old";
            command.AddParam("@current", local.Id);
            command.AddParam("@old", duplicate.Id);
            await command.ExecuteNonQueryAsync();
        }
        var foreignDesign = await primaryDesigns.CreateAsync(new(foreign.Id, DesignArtifactKind.Wireframe, "Foreign", "foreign design", "html"));
        var orphan = await primaryDesigns.CreateAsync(new("missing-spec", DesignArtifactKind.Wireframe, "Orphan", "orphan design", "html"));
        var misplaced = await localDesigns.CreateAsync(new(foreign.Id, DesignArtifactKind.Wireframe, "Misplaced", "misplaced design", "html"));
        var localArt = await new ArtOutputStore(fixture.Secondary.Db).CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "Current", "current/model.glb", "glb"));
        var historicalArt = await new ArtOutputStore(fixture.Primary.Db).CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "Historical", "historical/model.glb", "glb"));
        var foreignArt = await new ArtOutputStore(fixture.Primary.Db).CreateAsync(new(foreign.Id, ArtOutputKind.Mesh, "Foreign", "foreign/model.glb", "glb"));
        var expected = new Dictionary<string, string?>
        {
            [spec.Id] = spec.Body, [local.Id] = local.Body, [history.Id] = history.Body,
            [localArt.Id] = localArt.Body, [historicalArt.Id] = historicalArt.Body,
            [foreign.Id] = null, [foreignDesign.Id] = null, [orphan.Id] = null,
            [misplaced.Id] = null, [foreignArt.Id] = null,
        };
        using var runtime = await fixture.Build("second", legacyAssignment ? null : "primary");
        var client = await Read(runtime, "second", expected.Keys.ToArray());
        foreach (var (id, body) in expected)
        {
            using var result = JsonDocument.Parse(client.Results[id]);
            Assert.Equal(body ?? "not_found", result.RootElement.GetProperty(body is null ? "error" : "body").GetString());
        }
        Assert.Equal("obsolete historical copy", (await primaryDesigns.GetAsync(local.Id))!.Body);
        Assert.Null(await localDesigns.GetAsync(history.Id));
        Assert.Null(await new SpecStore(fixture.Primary).GetAsync(spec.Id));
        Assert.Equal(5, (await new ContextHandoffStore(fixture.Secondary.Db).ListForTaskAsync("")).Count);
        Assert.Empty(await new ContextHandoffStore(fixture.Primary.Db).ListForTaskAsync(""));
        Assert.DoesNotContain("aaa-added-later", fixture.OpenedProjects);
    }

    [Fact]
    public async Task PrimaryWorkerRetainsLocalReadsWhenHistoryUsesTheSameStore()
    {
        await using var fixture = await Fixture.Create();
        var spec = await new SpecStore(fixture.Primary).CreateAsync(new NewSpec("stale", "Owned", "primary spec"));
        var design = await new DesignArtifactStore(fixture.Primary.Db).CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Owned", "primary design", "html"));
        using var runtime = await fixture.Build("primary", "primary");
        var client = await Read(runtime, "primary", [design.Id]);
        Assert.Contains("primary design", client.Results[design.Id]);
    }

    [Fact]
    public async Task UnknownPinnedHistoricalSourcePreservesLocalReadsWithoutChoosingAnotherDatabase()
    {
        await using var fixture = await Fixture.Create();
        var spec = await new SpecStore(fixture.Secondary).CreateAsync(new NewSpec("second", "Owned", "local spec"));
        var local = await new DesignArtifactStore(fixture.Secondary.Db).CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Current", "local design", "html"));
        var historical = await new DesignArtifactStore(fixture.Primary.Db).CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Historical", "historical design", "html"));
        using var runtime = await fixture.Build("second", "missing-project");
        var client = await Read(runtime, "second", [local.Id, historical.Id]);
        Assert.Contains("local design", client.Results[local.Id]);
        Assert.Contains("not_found", client.Results[historical.Id]);
        Assert.DoesNotContain("missing-project", fixture.OpenedProjects);
        Assert.DoesNotContain("primary", fixture.OpenedProjects);
    }

    [Fact]
    public async Task WorkerProjectMismatchDoesNotOfferArtifactReads()
    {
        await using var fixture = await Fixture.Create();
        using var runtime = await fixture.Build("second", "primary");
        var client = await Read(runtime, "primary", []);
        Assert.False(client.ArtifactToolOffered);
    }

    private static async Task<ArtifactReadClient> Read(WorkerRuntime runtime, string project, string[] ids)
    {
        // Replace only the provider transport. All worker composition and tool
        // registration run through WorkerHost.BuildRunner; no model HTTP occurs.
        var runner = Assert.IsType<MafAgentRunner>(runtime.Runner);
        var field = typeof(MafAgentRunner).GetField("_chatClientFactory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var factory = Assert.IsType<OpenAICompatibleChatClientFactory>(field.GetValue(runner));
        var client = new ArtifactReadClient(ids);
        factory.RequestPolicy = (_, _) => client;
        await runner.RunAsync(AgentType.Reviewer, "Read the referenced artifacts", null,
            new Dictionary<string, object> { ["projectId"] = project, ["workerAttemptId"] = "attempt" }, default);
        Assert.Equal(ids.Length, client.Results.Count);
        return client;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = TempRoot.Instance.NewDirectory("worker-history");
        private readonly IssueStore _registry = new(TempRoot.Instance.NewDbPath("worker-history-registry"));
        private readonly Dictionary<string, IssueStore> _stores = [];
        public IssueStore Primary => _stores["primary"];
        public IssueStore Secondary => _stores["second"];
        public List<string> OpenedProjects { get; } = [];
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.Register("primary");
            await fixture.Register("second");
            return fixture;
        }
        public async Task Register(string id)
        {
            _stores[id] = new IssueStore(Path.Combine(_root, id + ".db"), id);
            var projects = new ProjectStore(_registry);
            await projects.UpsertAsync(new NewProject(id, id, "https://example.invalid/fixture.git"));
            await projects.UpdateLocalPathAsync(id, _root);
        }
        public async Task<WorkerRuntime> Build(string project, string? historical)
        {
            var context = new Dictionary<string, string> { ["projectId"] = project };
            var assignmentJson = historical is null
                ? JsonSerializer.Serialize(new { Role = AgentType.Reviewer, Prompt = "Read", SessionId = (string?)null, Context = context })
                : JsonSerializer.Serialize(new WorkerInvocation(AgentType.Reviewer, "Read", null, context, historical));
            if (historical is null) Assert.Null(JsonSerializer.Deserialize<WorkerInvocation>(assignmentJson)!.HistoricalProjectId);
            var assignment = new WorkerAssignment("attempt", "dataset", "adoption", project, "task", "Reviewer", "dispatch",
                new string('a', 40), _root, assignmentJson, "token");
            var options = new AgentOptions
            {
                Forgesystem = new ForgesystemOptions { DataRoot = _root },
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
            IDbConnectionFactory Database(string id)
            {
                OpenedProjects.Add(id);
                return _stores[id].Db;
            }
            return await WorkerHost.BuildRunner(options, assignment, new WorkerAttemptStore(_registry.Db),
                NullLoggerFactory.Instance, default, _registry.Db, Database);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var store in _stores.Values) await store.DisposeAsync();
            await _registry.DisposeAsync();
        }
    }

    private sealed class RecordingJobs(WorkerAttemptStore store) : IWorkerJobs
    {
        public bool FailCreation { get; set; } = true;
        public List<WorkerAssignment> Created { get; } = [];
        public async Task EnsureJobAsync(WorkerAssignment assignment, CancellationToken ct)
        {
            Created.Add(assignment);
            if (FailCreation) throw new HttpRequestException("Creation response lost before a pod claims the attempt.");
            Assert.True(await store.ClaimAsync(assignment.Id, "pod", ct));
            Assert.True(await store.CompleteAsync(assignment.Id, "pod", assignment.AttemptToken,
                JsonSerializer.Serialize(new AgentRunResult("done", null, 0, 0, TimeSpan.Zero)), null, ct));
        }
        public Task<string?> GetFailureAsync(string attemptId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task CancelAndWaitAsync(string attemptId, CancellationToken ct) => Task.CompletedTask;
    }
}
