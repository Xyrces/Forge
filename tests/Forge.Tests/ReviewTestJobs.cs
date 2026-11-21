using System.Text.Json;
using Forge.Agents;
using Forge.Core.Execution;
using Forge.Orchestrator.Workers;

namespace Forge.Tests;

internal sealed class ReviewTestJobs(WorkerAttemptStore store) : IWorkerJobs
{
    public List<WorkerAssignment> Created { get; } = [];
    public List<string> Joined { get; } = [];
    public Func<WorkerAssignment, Task>? OnCreate { get; set; }
    public Func<string, Task>? OnObserve { get; set; }
    public Action<string>? OnJoin { get; set; }
    public async Task EnsureJobAsync(WorkerAssignment assignment, CancellationToken ct)
    {
        Created.Add(assignment);
        await store.ClaimAsync(assignment.Id, "pod-" + assignment.Id, ct);
        if (OnCreate is not null) await OnCreate(assignment);
    }
    public async Task Finish(WorkerAssignment assignment, string text = "", string? error = null)
    {
        await store.CompleteAsync(assignment.Id, "pod-" + assignment.Id, assignment.AttemptToken,
            error is null ? JsonSerializer.Serialize(new AgentRunResult(text, null, 0, 0, TimeSpan.Zero)) : null, error);
    }
    public async Task<string?> GetFailureAsync(string attemptId, CancellationToken ct)
    {
        if (OnObserve is not null) await OnObserve(attemptId);
        return null;
    }
    public Task CancelAndWaitAsync(string attemptId, CancellationToken ct)
    {
        OnJoin?.Invoke(attemptId);
        Joined.Add(attemptId);
        return Task.CompletedTask;
    }
}
