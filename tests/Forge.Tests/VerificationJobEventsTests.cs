using System.Text.Json;
using Forge.Core.Execution;
using Forge.Core.Messaging;
using Forge.Orchestrator.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class VerificationJobEventsTests
{
    [Theory]
    [InlineData("Complete")]
    [InlineData("Failed")]
    public async Task OnlyMatchingTerminalJobsWakeTheirOwnTaskAndSprint(string condition)
    {
        var publisher = new Events();
        var assignment = new WorkerAssignment("attempt", "dataset", "adopt", "secondary", "client-3",
            "QA", "integration-verify-123", "head", "path", "{}", "token");
        using var http = new HttpClient();
        var observer = new VerificationJobEvents(http, "forge", "dataset",
            (id, _) => Task.FromResult<WorkerAssignment?>(id == assignment.Id ? assignment : null),
            publisher, NullLogger<VerificationJobEvents>.Instance);

        JsonElement Job(string name, string state) => JsonSerializer.SerializeToElement(new
        {
            metadata = new { name, labels = new Dictionary<string, string> { ["forge-attempt"] = "attempt" } },
            status = new { conditions = new[] { new { type = condition, status = state } } },
        });
        await observer.ObserveAsync(Job("another-job", "True"), default);
        await observer.ObserveAsync(Job("forge-agent-attempt", "False"), default);
        Assert.Empty(publisher.Hints);
        await observer.ObserveAsync(Job("forge-agent-attempt", "True"), default);
        var watch = Assert.Single(publisher.Hints.OfType<PrWatchRequested>());
        Assert.Equal("secondary", watch.ProjectId);
        Assert.Equal("client-3", watch.TaskId);
        Assert.Equal(SweepKind.Assemble, Assert.Single(publisher.Hints.OfType<SweepTick>()).Kind);

        var first = publisher.Hints.Select(h => h.MessageId).ToArray();
        publisher.Hints.Clear();
        await observer.ObserveAsync(Job("forge-agent-attempt", "True"), default);
        Assert.Equal(first, publisher.Hints.Select(h => h.MessageId).ToArray());

        publisher.Hints.Clear();
        assignment = assignment with { DatasetId = "different" };
        await observer.ObserveAsync(Job("forge-agent-attempt", "True"), default);
        Assert.Empty(publisher.Hints);
    }

    private sealed class Events : IEventPublisher
    {
        public List<IForgeEvent> Hints { get; } = [];
        public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
        {
            Hints.Add(evt);
            return Task.CompletedTask;
        }
    }
}
