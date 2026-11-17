using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class GroomingCapacityTests
{
    [Fact]
    public async Task OwningProjectCapacitiesAreReadForEachRunAndVerificationRolesAreExcluded()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("capacity"), "other");
        var specs = new SpecStore(issues);
        var client = new CaptureClient();
        var capacities = new Dictionary<string, int> { ["coredev"] = 3, ["clientdev"] = 0, ["qa"] = 77, ["reviewer"] = 99 };
        var lookedUp = new List<string>();
        var factory = Factory(issues, specs, client, (id, ct) =>
        {
            lookedUp.Add(id);
            return Task.FromResult<IReadOnlyDictionary<string, int>>(capacities);
        });
        await Run(factory, specs);
        Assert.Contains("coredev: 3 concurrent workers", client.Prompt);
        Assert.Contains("clientdev: 0 concurrent workers", client.Prompt);
        Assert.DoesNotContain("reviewer: 99 concurrent workers", client.Prompt);
        Assert.DoesNotContain("qa: 77 concurrent workers", client.Prompt);
        Assert.Contains("Assign genuine automated test-code changes to CoreDev or ClientDev", client.Prompt);
        Assert.Contains("not an artificial three-task minimum", client.Prompt);
        Assert.Contains("never a fill target", client.Prompt);
        capacities["coredev"] = 1;
        await Run(factory, specs);
        Assert.Contains("coredev: 1 concurrent workers", client.Prompt);
        Assert.Equal(new[] { "other", "other" }, lookedUp);
    }

    [Fact]
    public async Task MissingOverridesUseSharedDefaults()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("capacity"), "other");
        var specs = new SpecStore(issues);
        var client = new CaptureClient();
        await Run(Factory(issues, specs, client, null), specs);
        Assert.Contains("coredev: 2 concurrent workers", client.Prompt);
        Assert.Contains("clientdev: 2 concurrent workers", client.Prompt);
        Assert.DoesNotContain("qa: 1 concurrent workers", client.Prompt);
        Assert.Contains("QA verifies an existing PR in the optional watch lane", client.Prompt);
        Assert.DoesNotContain("reviewer: 2", client.Prompt);
    }

    private static GroomerAgentFactory Factory(IssueStore issues, SpecStore specs, CaptureClient client,
        Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>>>? lookup) =>
        new(issues, specs, new InMemoryDashboardEventBus(), new ScriptingChatClientFactory(client),
            new LlmConfig(new ProviderConfig("test", "", null, null, "test")), NullLoggerFactory.Instance,
            roleCapacitiesLookup: lookup);

    private static async Task Run(GroomerAgentFactory factory, SpecStore specs)
    {
        var spec = await specs.CreateAsync(new NewSpec("other", "Approved work", "Implement behavior"));
        await specs.SetStatusAsync(spec.Id, SpecStatus.Approved);
        await Assert.ThrowsAsync<OperationCanceledException>(() => factory.Create(projectId: "other").GroomAsync(spec.Id));
    }

    private sealed class CaptureClient() : DelegatingChatClient(new MultiToolCallingChatClient([], "unused"))
    {
        public string Prompt { get; private set; } = "";
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompt = options?.Instructions + "\n" + string.Join("\n", messages.Select(m => m.Text));
            throw new OperationCanceledException("Stop after capturing the actual model prompt.");
        }
    }
}

