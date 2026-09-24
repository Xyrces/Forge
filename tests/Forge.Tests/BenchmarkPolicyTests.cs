using System.Text.Json;
using Forge.Agents;
using Forge.Core;
using Forge.Tools.E2E;
using Microsoft.Extensions.AI;
using Xunit;

namespace Forge.Tests;

public sealed class BenchmarkPolicyTests : IDisposable
{
    private readonly string _root = TempRoot.Instance.NewDirectory("benchmark-policy");
    private static readonly LlmConfig IgnoredConfig = new(
        new ProviderConfig("ignored", "https://ignored.example", null, null, "ignored"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BENCHMARK_ENGINEER_KEY", null);
        Environment.SetEnvironmentVariable("BENCHMARK_CRITIC_KEY", null);
        Environment.SetEnvironmentVariable("BENCHMARK_REVIEWER_KEY", null);
        Environment.SetEnvironmentVariable("BENCHMARK_ESCALATION_KEY", null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task RoutesRolesAndEscalationWhileSharingPerModelMeters()
    {
        var fake = new RecordingFactory();
        var runtime = LoadPolicy(includeEscalation: true, requireCredentials: false)
            .CreateRuntime(_root, fake);

        var engineerA = runtime.Factory.Create(IgnoredConfig, AgentType.CoreDev);
        var engineerB = runtime.Factory.Create(IgnoredConfig, AgentType.CoreDev);
        var critic = runtime.Factory.Create(IgnoredConfig, AgentType.Reviewer);
        var finalReviewer = runtime.CreateReviewerClient();
        await engineerA.GetResponseAsync([new ChatMessage(ChatRole.User, "one")]);
        await engineerB.GetResponseAsync([new ChatMessage(ChatRole.User, "two")]);
        await critic.GetResponseAsync([new ChatMessage(ChatRole.User, "three")]);
        await finalReviewer.GetResponseAsync([new ChatMessage(ChatRole.User, "four")]);
        runtime.UseEscalation();
        await runtime.Factory.Create(IgnoredConfig, AgentType.CoreDev)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "five")]);

        Assert.Equal(["engineer-model", "engineer-model", "critic-model", "reviewer-model", "escalation-model"],
            fake.CreatedModels);
        Assert.Equal(2, runtime.Snapshots["engineer"].Calls);
        Assert.Equal(1, runtime.Snapshots["critic"].Calls);
        Assert.Equal(1, runtime.Snapshots["reviewer"].Calls);
        Assert.Equal(1, runtime.Snapshots["escalation"].Calls);
        Assert.Equal(5, runtime.AggregateSnapshot.Calls);
        Assert.Equal("escalation", runtime.ActiveEngineerModel.Id);
        Assert.Equal(2, runtime.MaxEngineeringAttempts);

        using var ledger = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_root, "state", "usage-reviewer.json")));
        Assert.Equal(
            "FinalReviewer",
            ledger.RootElement.GetProperty("calls")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task MissingUsageHaltsPolicyBeforeAnotherModelCanBeCreated()
    {
        var fake = new RecordingFactory { OmitUsageOnFirstCall = true };
        var runtime = LoadPolicy(includeEscalation: false, requireCredentials: false)
            .CreateRuntime(_root, fake);
        var engineer = runtime.Factory.Create(IgnoredConfig, AgentType.CoreDev);
        var critic = runtime.Factory.Create(IgnoredConfig, AgentType.Reviewer);

        await critic.GetResponseAsync([new ChatMessage(ChatRole.User, "one")]);

        Assert.True(runtime.HasProviderOrAccountingFailure);
        Assert.False(runtime.AggregateSnapshot.AccountingComplete);
        Assert.Throws<InvalidOperationException>(() => runtime.CreateReviewerClient());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engineer.GetResponseAsync([new ChatMessage(ChatRole.User, "must not reach provider")]));
        Assert.Equal(1, fake.ProviderCalls);
        Assert.Equal(2, fake.CreatedModels.Count);
    }

    [Fact]
    public async Task CriticAndFinalReviewerShareOneModelCallLimit()
    {
        var document = PolicyDocument(includeEscalation: false);
        var models = (List<Dictionary<string, object?>>)document["models"]!;
        models.RemoveAll(model => Equals(model["id"], "reviewer"));
        models.Single(model => Equals(model["id"], "critic"))["maxCalls"] = 1;
        var roles = (Dictionary<string, object?>)document["roles"]!;
        roles["reviewer"] = "critic";
        var runtime = BenchmarkPolicy.Load(WriteJson("shared.json", document), false)
            .CreateRuntime(_root, new RecordingFactory());
        var critic = runtime.Factory.Create(IgnoredConfig, AgentType.Reviewer);
        var reviewer = runtime.CreateReviewerClient();

        await critic.GetResponseAsync([new ChatMessage(ChatRole.User, "plan")]);
        await Assert.ThrowsAsync<BenchmarkLimitExceededException>(() =>
            reviewer.GetResponseAsync([new ChatMessage(ChatRole.User, "final") ]));

        Assert.Equal(1, runtime.Snapshots["critic"].Calls);
        Assert.True(runtime.HasProviderOrAccountingFailure);
    }

    [Fact]
    public void LoadRequiresEveryCredentialBeforeRuntimeOrCalls()
    {
        Environment.SetEnvironmentVariable("BENCHMARK_ENGINEER_KEY", "present");
        var path = WritePolicy(includeEscalation: false);

        var error = Assert.Throws<InvalidDataException>(() =>
            BenchmarkPolicy.Load(path, requireCredentials: true));

        Assert.Contains("BENCHMARK_CRITIC_KEY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRejectsUnknownRoleReferencesAndUnsafeUrls()
    {
        var unknownRole = PolicyDocument(includeEscalation: false);
        unknownRole["roles"] = new Dictionary<string, object?>
        {
            ["engineer"] = "missing",
            ["critic"] = "critic",
            ["reviewer"] = "reviewer",
        };
        var unknownPath = WriteJson("unknown.json", unknownRole);
        Assert.Throws<InvalidDataException>(() => BenchmarkPolicy.Load(unknownPath, false));

        var unsafeUrl = PolicyDocument(includeEscalation: false);
        var models = (List<Dictionary<string, object?>>)unsafeUrl["models"]!;
        models[0]["baseUrl"] = "http://127.0.0.1:8080/v1";
        var unsafePath = WriteJson("unsafe.json", unsafeUrl);
        Assert.Throws<InvalidDataException>(() => BenchmarkPolicy.Load(unsafePath, false));
    }

    [Fact]
    public void LoadAcceptsInvariantStringRatesAndRejectsCredentialRetargeting()
    {
        var validPath = WritePolicy(includeEscalation: false);
        var policy = BenchmarkPolicy.Load(validPath, requireCredentials: false);
        Assert.Equal(1.25m, policy.Models["engineer"].InputUsdPerMillion);

        var collision = PolicyDocument(includeEscalation: false);
        var models = (List<Dictionary<string, object?>>)collision["models"]!;
        models[1]["apiKeyEnv"] = "BENCHMARK_ENGINEER_KEY";
        var collisionPath = WriteJson("collision.json", collision);

        var error = Assert.Throws<InvalidDataException>(() => BenchmarkPolicy.Load(collisionPath, false));
        Assert.Contains("cannot target different", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRejectsModelsThatNoRoleUses()
    {
        var document = PolicyDocument(includeEscalation: false);
        var models = (List<Dictionary<string, object?>>)document["models"]!;
        models.Add(Model("unused", "unused-model", "BENCHMARK_UNUSED_KEY"));

        var error = Assert.Throws<InvalidDataException>(() =>
            BenchmarkPolicy.Load(WriteJson("unused.json", document), false));

        Assert.Contains("unused", error.Message, StringComparison.Ordinal);
    }

    private BenchmarkPolicy LoadPolicy(bool includeEscalation, bool requireCredentials) =>
        BenchmarkPolicy.Load(WritePolicy(includeEscalation), requireCredentials);

    private string WritePolicy(bool includeEscalation) =>
        WriteJson("policy.json", PolicyDocument(includeEscalation));

    private string WriteJson(string name, object value)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
        return path;
    }

    private static Dictionary<string, object?> PolicyDocument(bool includeEscalation)
    {
        var models = new List<Dictionary<string, object?>>
        {
            Model("engineer", "engineer-model", "BENCHMARK_ENGINEER_KEY"),
            Model("critic", "critic-model", "BENCHMARK_CRITIC_KEY"),
            Model("reviewer", "reviewer-model", "BENCHMARK_REVIEWER_KEY"),
        };
        var roles = new Dictionary<string, object?>
        {
            ["engineer"] = "engineer",
            ["critic"] = "critic",
            ["reviewer"] = "reviewer",
        };
        if (includeEscalation)
        {
            models.Add(Model("escalation", "escalation-model", "BENCHMARK_ESCALATION_KEY"));
            roles["escalation"] = "escalation";
        }
        return new Dictionary<string, object?>
        {
            ["id"] = "mixed-policy",
            ["models"] = models,
            ["roles"] = roles,
            ["maxEngineeringAttempts"] = 2,
        };
    }

    private static Dictionary<string, object?> Model(string id, string model, string key) => new()
    {
        ["id"] = id,
        ["provider"] = id + "-provider",
        ["model"] = model,
        ["baseUrl"] = $"https://{id}.example/v1",
        ["apiKeyEnv"] = key,
        ["maxCalls"] = 4,
        ["maxInputTokens"] = 10_000,
        ["maxOutputTokens"] = 1_000,
        ["inputUsdPerMillion"] = "1.25",
        ["outputUsdPerMillion"] = "4.5",
    };

    private sealed class RecordingFactory : IChatClientFactory
    {
        private int _calls;
        public List<string> CreatedModels { get; } = [];
        public int ProviderCalls => Volatile.Read(ref _calls);
        public bool OmitUsageOnFirstCall { get; init; }

        public IChatClient Create(
            LlmConfig config,
            AgentType role,
            string? projectId = null,
            RoleModel? modelOverride = null)
        {
            var (_, model) = config.Resolve(role);
            CreatedModels.Add(model);
            return new RecordingClient(this);
        }

        private sealed class RecordingClient(RecordingFactory owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var call = Interlocked.Increment(ref owner._calls);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    Usage = owner.OmitUsageOnFirstCall && call == 1
                        ? null
                        : new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 },
                });
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }
}
