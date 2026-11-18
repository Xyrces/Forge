using Forge.Core;
using Forge.Core.Messaging;
using Forge.Projects;
using Forge.Reviewer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Forge.Dashboard;

public sealed record HumanReviewRequest(bool Required);
public sealed record ReviewPolicyRequest(string Mode, System.Text.Json.JsonElement NominatedHuman = default);

public static class TaskReviewEndpoints
{
    public static void MapTaskReviewEndpoints(WebApplication app, IIssueStore primary,
        ProjectContextFactory? projects, IEventPublisher? publisher, ILogger logger)
    {
        IssueStore? Resolve(string projectId) => projects is not null ? projects.Find(projectId)?.Issues as IssueStore
            : primary is IssueStore concrete && concrete.ProjectId == projectId ? concrete : null;

        app.MapGet("/api/tasks/{id}/human-review", async (string id, string projectId, CancellationToken ct) =>
        {
            var store = Resolve(projectId);
            if (store is null || await store.GetAsync(id, ct) is null) return Results.NotFound();
            await using var memory = new MemoryStore(store.Db);
            var policy = new PullRequestReviewPolicy(memory);
            var hold = await policy.GetHoldAsync(id, ct);
            var settings = await policy.GetSettingsAsync(ct);
            return Results.Ok(new { hold.Required, hold.SyncPending, mode = settings.Mode.ToString(), settings.NominatedHuman });
        });

        app.MapPut("/api/tasks/{id}/human-review", async (string id, string projectId,
            HumanReviewRequest request, HttpContext context, CancellationToken ct) =>
        {
            if (context.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string actor)
                return Results.Unauthorized();
            var store = Resolve(projectId);
            if (store is null) return Results.NotFound();
            var gate = PRWatcher.GetPollGate(projectId, id);
            await gate.WaitAsync(ct);
            var saved = false;
            try
            {
                var task = await store.GetAsync(id, ct);
                if (task is null) return Results.NotFound();
                if (task.Status is IssueStatus.Completed or IssueStatus.Closed)
                    return Results.Conflict(new { error = "This task is already complete or closed; its review history is read-only." });
                if (AgentTaskTypes.IsContainer(task.Type) || task.Type == AgentTaskTypes.PrWatch)
                    return Results.BadRequest(new { error = "Human review is reserved for engineering tasks." });
                await using var memory = new MemoryStore(store.Db);
                var policy = new PullRequestReviewPolicy(memory);
                var hold = new HumanReviewHold(request.Required, true);
                await policy.SetHoldAsync(id, hold, actor, ct);
                saved = true;
                var settings = await policy.GetSettingsAsync(ct);
                try
                {
                    await store.PatchReviewMetadataAsync(id, new Dictionary<string, object>
                    {
                        ["humanReviewRequired"] = request.Required || settings.Mode is RequiredReview.Human or RequiredReview.Both,
                        ["humanReviewNominee"] = settings.NominatedHuman ?? "",
                        ["humanReviewTaskHold"] = request.Required,
                        ["humanReviewSyncPending"] = true,
                        ["humanReviewRequestedBy"] = actor,
                        ["humanReviewRequestedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    }, request.Required ? "Human review required before merge." : "Additional task human-review requirement released; project policy still applies.", ct, actor);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Review hold saved but task display patch failed for {Project}/{Task}", projectId, id);
                    return Results.Problem("Review requirement was saved, but its task display could not be updated. Reload to check synchronization.", statusCode: 503);
                }
                return Results.Ok(new { hold.Required, hold.SyncPending, mode = settings.Mode.ToString(), settings.NominatedHuman });
            }
            finally
            {
                gate.Release();
                // Publish after releasing: the watcher uses a nonblocking gate
                // and would otherwise consume this hint while still locked out.
                if (saved) await PrWatchWakeup.RequestAsync(publisher ?? store.Events, projectId, id,
                    "operator changed human review requirement", ct);
            }
        });

        app.MapGet("/api/projects/{id}/review-policy", async (string id, CancellationToken ct) =>
        {
            var store = Resolve(id);
            if (store is null) return Results.NotFound();
            await using var memory = new MemoryStore(store.Db);
            var settings = await new PullRequestReviewPolicy(memory).GetSettingsAsync(ct);
            return Results.Ok(new { mode = settings.Mode.ToString(), settings.NominatedHuman });
        });

        app.MapPut("/api/projects/{id}/review-policy", async (string id, ReviewPolicyRequest request,
            HttpContext context, CancellationToken ct) =>
        {
            if (context.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string actor)
                return Results.Unauthorized();
            if (!Enum.TryParse<RequiredReview>(request.Mode, true, out var mode) || !Enum.IsDefined(mode)
                || int.TryParse(request.Mode, out _))
                return Results.BadRequest(new { error = "Choose Automated, Human, or Both." });
            var store = Resolve(id);
            if (store is null) return Results.NotFound();
            var gates = PRWatcher.GetPollGatesForTasks(id, (await store.ListAsync(new IssueFilter(), ct))
                .Where(t => t.Status is not (IssueStatus.Completed or IssueStatus.Closed)).Select(t => t.Id));
            var held = new List<SemaphoreSlim>();
            var saved = false;
            try
            {
                foreach (var gate in gates) { await gate.WaitAsync(ct); held.Add(gate); }
                await using var memory = new MemoryStore(store.Db);
                var policy = new PullRequestReviewPolicy(memory);
                string? nominee;
                try
                {
                    nominee = request.NominatedHuman.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.Undefined => (await policy.GetSettingsAsync(ct)).NominatedHuman,
                        System.Text.Json.JsonValueKind.Null => null,
                        System.Text.Json.JsonValueKind.String => PullRequestReviewPolicy.NormalizeNominee(request.NominatedHuman.GetString()),
                        _ => throw new ArgumentException("Nominated human must be a GitHub login or null.")
                    };
                }
                catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
                await policy.SetAsync(mode, nominee, actor, ct);
                saved = true;
                return Results.Ok(new { mode = mode.ToString(), nominatedHuman = nominee });
            }
            finally
            {
                foreach (var gate in held.AsEnumerable().Reverse()) gate.Release();
                if (saved) await (publisher ?? store.Events).PublishAsync(new SweepTick
                {
                    MessageId = $"review-mode:{id}:{Guid.NewGuid():N}", ProjectId = id, Kind = SweepKind.Watch,
                }, ct);
            }
        });
    }
}
