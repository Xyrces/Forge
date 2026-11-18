using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Forge.Agents;
using Forge.Core;
using Forge.Orchestrator;

namespace Forge.Dashboard;

/// <summary>
/// P6 Stage 9 — Engineering Dispatch workflow endpoints.
///   GET   /api/tasks/in-progress           -> full task row + last 10 events
///   POST  /api/tasks/{id}/retry-message   -> inject a string into AgentMessageBus
///   POST  /api/tasks/{id}/recover         -> per-task recovery run
/// </summary>
public static class TaskEndpoints
{
    public sealed record TaskEventDto(string Kind, DateTime At, string? Detail, string? Actor = null, long? Id = null);

    public sealed record InProgressTaskDto(
        string Id,
        string Type,
        string Title,
        string Status,
        int Priority,
        string? Assignee,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? ClosedAt,
        string? DispatchCheckpoint,
        int RecoveryAttempts,
        string? PrUrl,
        string? Branch,
        string? WorktreePath,
        IReadOnlyList<TaskEventDto> Events);

    public static void MapTaskEndpoints(
        WebApplication app,
        IIssueStore issues,
        AgentMessageBus? messageBus,
        Orchestrator.StartupRecovery? recovery,
        ILogger logger,
        Projects.ProjectContextFactory? projectContexts = null,
        ISprintStore? sprints = null,
        AgentRunStore? runs = null,
        Forge.Core.Workflow.WorkflowResolver? workflow = null,
        Forge.Core.TaskStateMachine? lifecycle = null,
        Func<string, GitHubService?>? gitHubForProject = null,
        Func<string, string, CancellationToken, Task<bool>>? hasActiveWorker = null,
        Func<string, string, CancellationToken, Task<IReadOnlyList<Core.Execution.WorkerAttemptStore.AttemptSummary>>>? workerHistory = null)
    {
        TaskRegroomEndpoints.MapTaskRegroomEndpoints(app, issues, projectContexts, logger);
        TaskReviewHistoryEndpoints.Map(app, issues, projectContexts, gitHubForProject);
        app.MapGet("/api/tasks/{id}/workers", async (string id, string projectId, CancellationToken ct) =>
        {
            var store = projectContexts?.Find(projectId)?.Issues ?? (issues is IssueStore primary && primary.ProjectId == projectId ? primary : null);
            if (store is null || await store.GetAsync(id, ct) is null) return Results.NotFound();
            if (workerHistory is null) return Results.Json(new { available = false, attempts = Array.Empty<object>() });
            return Results.Json(new { available = true, attempts = await workerHistory(projectId, id, ct), limit = 100 });
        });
        app.MapGet("/api/tasks/{id}/history", async (string id, string? projectId, string? cursor, CancellationToken ct) =>
        {
            var store = projectId is not null && projectContexts is not null ? projectContexts.Find(projectId)?.Issues : issues;
            if (store is null || await store.GetAsync(id, ct) is null) return Results.NotFound();
            if (store is not IssueStore concrete) return Results.StatusCode(503);
            try
            {
                var page = await new TaskHistoryReadStore(concrete.Db).ReadAsync(projectId ?? concrete.ProjectId ?? "primary", id, cursor, ct);
                return Results.Json(new { events = page.Events.Select(e => new TaskEventDto(e.Kind, e.Timestamp, e.Detail, e.Actor, e.Id)), page.NextCursor });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapGet("/api/tasks/list", async (string? projectId, string? view, string? search, string? cursor, CancellationToken ct) =>
        {
            var store = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var context = projectContexts.Find(projectId);
                if (context is null) return Results.NotFound(new { error = "project not found" });
                store = context.Issues;
            }
            var records = (await store.ListAsync(new IssueFilter(), ct))
                .Where(t => t.Type != AgentTaskTypes.PrWatch && t.Type is not ("epic" or "story"))
                .Where(t => view switch
                {
                    "history" => t.Status is IssueStatus.Completed or IssueStatus.Closed,
                    "attention" => t.Status is IssueStatus.Failed or IssueStatus.Blocked || TaskSituation.Describe(t).Label == "Waiting for human review",
                    _ => t.Status is IssueStatus.Pending or IssueStatus.InProgress
                })
                .Where(t => string.IsNullOrWhiteSpace(search) || t.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || t.Id.Contains(search, StringComparison.OrdinalIgnoreCase) || t.GetMetadata("prNumber") == search.TrimStart('#'))
                .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
            var start = cursor is null ? 0 : records.FindIndex(t => t.Id == cursor) + 1;
            if (cursor is not null && start == 0) return Results.BadRequest(new { error = "History changed. Refresh the list." });
            var page = records.Skip(start).Take(50).ToArray();
            return Results.Json(new { items = page.Select(t => new { t.Id, t.Title, status = t.Status.ToString(), t.Type,
                prNumber = t.GetMetadata("prNumber"), situation = TaskSituation.Describe(t), t.UpdatedAt }),
                nextCursor = start + page.Length < records.Count ? page.LastOrDefault()?.Id : null, total = records.Count });
        });
        // Derived lifecycle state (Phase 1 read-model): what the task
        // is doing + what it's waiting on, projected from the task,
        // its PR-watch, and the live-run registry.
        app.MapGet("/api/tasks/{id}/state", async (string id, string? projectId, CancellationToken ct) =>
        {
            var store = issues;
            var runStore = runs;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx = projectContexts.Find(projectId);
                if (ctx is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx.Issues;
                runStore = new AgentRunStore(((IssueStore)ctx.Issues).Db);
            }
            var task = await store.GetAsync(id, ct);
            if (task is null) return Results.NotFound(new { error = "task not found", id });

            IssueRecord? watch = null;
            if (task.GetMetadata("prNumber") is not null)
            {
                var watches = await store.ListAsync(new IssueFilter { Type = AgentTaskTypes.PrWatch }, ct);
                watch = watches.FirstOrDefault(w =>
                    string.Equals(w.GetMetadata("taskId"), id, StringComparison.Ordinal)
                    && w.Status is not (IssueStatus.Completed or IssueStatus.Failed or IssueStatus.Closed));
            }
            var hasActiveDevRun = runStore is not null
                && (await runStore.ListActiveAsync(ct)).Any(r =>
                    string.Equals(r.TaskId, id, StringComparison.Ordinal)
                    && r.Role is "CoreDev" or "ClientDev");

            // Workflow policies (pass 3): strike budget + stall grace
            // come from the resolved definition when available.
            var wf = workflow is not null ? await workflow.ResolveAsync(ct) : null;
            var info = TaskStateProjector.Derive(task, watch, hasActiveDevRun, DateTime.UtcNow,
                maxStrikes: wf is not null
                    ? Forge.Core.Workflow.WorkflowPolicyReader.GetInt(
                        wf, Forge.Core.Workflow.WorkflowPolicies.MaxStrikes, TaskStateProjector.MaxStrikes)
                    : null,
                stallGrace: wf is not null
                    ? TimeSpan.FromMinutes(Forge.Core.Workflow.WorkflowPolicyReader.GetInt(
                        wf, Forge.Core.Workflow.WorkflowPolicies.StallGraceMinutes, (int)TaskStateProjector.StallGrace.TotalMinutes))
                    : null);
            return Results.Json(new
            {
                taskId = id,
                state = info.State.ToString(),
                substate = info.Substate,
                waitingOn = info.WaitingOn,
                strikes = info.Strikes,
                maxStrikes = info.MaxStrikes,
                situation = TaskSituation.Describe(task),
            });
        });

        // Single-task drill-down: the full row + parsed metadata +
        // the issue_event audit timeline + sprint membership. Powers
        // the /tasks/{id} page; every list view links here.
        app.MapGet("/api/tasks/{id}", async (string id, string? projectId, bool? includeEvents, CancellationToken ct) =>
        {
            var store = issues;
            ISprintStore? sprintStore = sprints;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx = projectContexts.Find(projectId);
                if (ctx is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx.Issues;
                sprintStore = ctx.Sprints;
            }
            var t = await store.GetAsync(id, ct);
            if (t is null) return Results.NotFound(new { error = "task_not_found", id });
            await using var reviewMemory = store is IssueStore owner ? new MemoryStore(owner.Db) : null;
            var nominee = reviewMemory is null ? null : (await new PullRequestReviewPolicy(reviewMemory).GetSettingsAsync(ct)).NominatedHuman;

            var events = includeEvents == false ? [] : await store.ListEventsAsync(t.Id, limit: 100, ct);
            var sprint = sprintStore is null ? null : await sprintStore.FindForIssueAsync(t.Id, ct);
            var plan = t.GetMetadata(Core.Completion.ExistingImplementationEvidence.MetadataKey) is not null && sprint is not null && store is IssueStore concrete
                ? await new SprintProposalAuditStore(concrete.Db).GetPlanForSprintAsync(sprint.Id, ct) : null;
            return Results.Json(new
            {
                id = t.Id,
                type = t.Type,
                title = t.Title,
                description = t.Description,
                status = t.Status.ToString(),
                priority = t.Priority,
                assignee = t.Assignee,
                parentIssueId = t.ParentIssueId,
                createdAt = t.CreatedAt,
                updatedAt = t.UpdatedAt,
                closedAt = t.ClosedAt,
                dispatchCheckpoint = t.DispatchCheckpoint?.ToString(),
                recoveryAttempts = t.RecoveryAttempts,
                situation = TaskSituation.Describe(t, plan, nominee),
                delivery = TaskDeliveryPresenter.For(t, plan),
                metadata = TaskEndpoints.ParseMetadata(t.MetadataJson),
                sprint = sprint is null ? null : new { id = sprint.Id, name = sprint.Name, status = sprint.Status.ToString() },
                events = events.Select(e => new TaskEventDto(e.Kind, e.Timestamp, e.Detail, e.Actor, e.Id)).ToArray(),
                historyLimited = events.Count == 100,
            });
        });

        app.MapGet("/api/tasks/in-progress", async (int? limit, string? projectId, CancellationToken ct) =>
        {
            try
            {
                // Multi-project: when ?projectId= is supplied and the
                // factory is available, read from that project's store;
                // otherwise fall back to the injected primary store.
                var store = issues;
                if (projectId is not null && projectContexts is not null)
                {
                    var ctx = projectContexts.Find(projectId);
                    if (ctx is null) return Results.NotFound(new { error = "project not found", projectId });
                    store = ctx.Issues;
                }
                var inFlight = await store.ListInProgressForRecoveryAsync(ct);
                var rows = new List<InProgressTaskDto>(inFlight.Count);
                foreach (var t in inFlight.Take(limit ?? 100))
                {
                    var events = await store.ListEventsAsync(t.Id, limit: 10, ct);
                    rows.Add(new InProgressTaskDto(
                        Id: t.Id,
                        Type: t.Type,
                        Title: t.Title,
                        Status: t.Status.ToString(),
                        Priority: t.Priority,
                        Assignee: t.Assignee,
                        CreatedAt: t.CreatedAt,
                        UpdatedAt: t.UpdatedAt,
                        ClosedAt: t.ClosedAt,
                        DispatchCheckpoint: t.DispatchCheckpoint?.ToString(),
                        RecoveryAttempts: t.RecoveryAttempts,
                        PrUrl: ExtractMeta(t.MetadataJson, "prUrl"),
                        Branch: ExtractMeta(t.MetadataJson, "branch"),
                        WorktreePath: ExtractMeta(t.MetadataJson, "worktreePath"),
                        Events: events.Select(e => new TaskEventDto(e.Kind, e.Timestamp, e.Detail)).ToArray()));
                }
                return Results.Json(rows);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/tasks/in-progress failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/retry-message", async (string id, string? projectId, HttpContext ctx) =>
        {
            if (messageBus is null) return Results.Problem(detail: "AgentMessageBus not configured", statusCode: 503);
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                if (!doc.RootElement.TryGetProperty("text", out var textEl))
                    return Results.BadRequest(new { error = "text required" });
                var text = textEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(text))
                    return Results.BadRequest(new { error = "text cannot be empty" });

                // The audit found this returned success for any id —
                // a typo'd task id must not silently succeed. Ids are
                // per-project sequences, so the existence check must
                // run against the OWNING project's store.
                var store = issues;
                if (projectId is not null && projectContexts is not null)
                {
                    var pctx = projectContexts.Find(projectId);
                    if (pctx is null) return Results.NotFound(new { error = "project not found", projectId });
                    store = pctx.Issues;
                }
                if (await store.GetAsync(id, ctx.RequestAborted) is null)
                    return Results.NotFound(new { error = "task not found", id });

                messageBus.Enqueue(id, text);
                return Results.Json(new { accepted = true, taskId = id });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/retry-message failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/requeue", async (string id, string? projectId, HttpContext ctx, CancellationToken ct) =>
        {
            // Operator requeue of a Failed task: the sanctioned path
            // (IssueStore transition + metadata update — never direct
            // SQL). Clears the failure bookkeeping (retryCount,
            // lastError(+At), noProgressAttempts) AND the rework
            // bookkeeping (reworkAttempts/Reason/Context) so both
            // breaker budgets start fresh — requeueing a
            // breaker-tripped task without clearing reworkAttempts
            // would let the next watch sweep re-trip it immediately.
            // Optional JSON body { reason, context }: seeds a guided
            // rework round (the dispatch prompt renders them as
            // "## Rework required") — used when the operator knows
            // exactly what the redispatch must do (e.g. "your PR is
            // approved, the worktree was rebuilt from main; fetch
            // your branch, merge main, push to retrigger CI").
            var store = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx2 = projectContexts.Find(projectId);
                if (ctx2 is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx2.Issues;
            }
            string? guideReason = null, guideContext = null;
            try
            {
                if (ctx.Request.ContentLength > 0)
                {
                    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("reason", out var rEl)) guideReason = rEl.GetString();
                        if (doc.RootElement.TryGetProperty("context", out var cEl)) guideContext = cEl.GetString();
                    }
                }
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON body" }); }
            try
            {
                var t = await store.GetAsync(id, ct);
                if (t is null) return Results.NotFound(new { error = "task not found", id });
                if (t.Status is not (IssueStatus.Failed or IssueStatus.Blocked))
                    return Results.Conflict(new { error = $"only Failed or Blocked tasks can be requeued (status is {t.Status})" });

                // One atomic transition: Failed -> Pending + clear the
                // failure bookkeeping so the retry budget starts fresh
                // (upsert-merge only: JSON null is the delete idiom).
                var meta = new Dictionary<string, object>
                {
                    ["retryCount"] = null!,
                    ["noProgressAttempts"] = null!,
                    ["lastError"] = null!,
                    ["lastErrorAt"] = null!,
                    ["reworkAttempts"] = null!,
                    ["reworkReason"] = null!,
                    ["reworkContext"] = null!,
                    // reworkForSha too: a fossil from an EARLIER round
                    // can equal the head the rework produces (the round
                    // pushes its fix AT that sha), and the watch guard
                    // then swallows every legitimate new rework verdict
                    // as "already queued for this head" (observed live
                    // 2026-08-18: task-651's RequestChanges review went
                    // unanswered — the Aug-09 fossil matched the pushed
                    // head and only the 35-min stall path saved it).
                    ["reworkForSha"] = null!,
                    // QA-stage budget reset (2026-08-24, task-740 loop):
                    // operator intervention restarts the per-head QA
                    // attempt budget exactly like the strike budgets —
                    // a qa-unavailable block must re-attempt QA at the
                    // same head after requeue, not instantly re-block.
                    ["qaAttempts"] = null!,
                    ["qaAttemptSha"] = null!,
                    ["qaStartedAt"] = null!,
                    ["requeuedFromFailedAt"] = DateTime.UtcNow.ToString("O"),
                    // Failure-ledger clearance marker: the triage
                    // consumer records this action on the open row.
                    ["clearanceAction"] = "operator-requeue",
                };
                if (!string.IsNullOrWhiteSpace(guideReason)) meta["reworkReason"] = guideReason;
                if (!string.IsNullOrWhiteSpace(guideContext)) meta["reworkContext"] = guideContext;
                // Clear the block marker when the block WAS the QA stage
                // — inert once Pending, but keeps the metadata honest
                // (the reviewer-unavailable marker stays: that block
                // kind has its own auto-resume bookkeeping).
                if (string.Equals(t.GetMetadata("blockedKind"),
                        Forge.Reviewer.QaDispatcher.BlockedKindQaUnavailable, StringComparison.Ordinal))
                    meta["blockedKind"] = null!;
                // A requeue IS the nudge: for a task with an open PR
                // the watch sweep re-adopts it immediately — and the
                // stale guard anchors to prOpenedAt, so a requeue of
                // an hours-old PR would trip "pr-stale" on the very
                // first poll (observed live 2026-07-30: task-12
                // re-Failed 3 minutes after requeue). The operator's
                // requeue is explicit progress intent — restart the
                // stale window.
                if (t.GetMetadata("prNumber") is not null)
                    meta["prOpenedAt"] = DateTime.UtcNow.ToString("O");
                await store.TransitionAsync(id, IssueStatus.Pending,
                    "operator requeue from Failed (failure + rework bookkeeping cleared)",
                    meta, ct);
                // The status transition alone leaves the lifecycle
                // state at Failed — and Failed+Dispatched is an
                // ILLEGAL machine transition, so the next run would
                // carry state=Failed for its whole life (observed
                // live 2026-08-01: task-18 "coredev live" in the
                // Needs-you lane). Report the requeue through the
                // machine so state goes back to Pending.
                if (lifecycle is not null)
                {
                    var requeued = await store.GetAsync(id, ct);
                    if (requeued is not null)
                        await lifecycle.ReportAsync(store, requeued, Forge.Core.TaskEvent.OperatorRequeue,
                            watch: null, hasActiveDevRun: false, ct);
                }
                logger.LogInformation("POST /api/tasks/{Id}/requeue: Failed -> Pending, failure + rework metadata cleared (guided={Guided})", id, guideReason is not null);
                return Results.Json(new { taskId = id, status = "Pending" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/requeue failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        // Operator reparent (2026-08-17): repair a broken parent link
        // — the groomer once accepted a bare numeric story id and wrote
        // parent_issue_id="39" instead of "story-39" (porthorizon
        // spec-257a4c26: 9 tasks orphaned; the stories looked taskless
        // and could never auto-close). Goes through IssueStore so the
        // event audit records the repair.
        app.MapPost("/api/tasks/{id}/reparent", async (string id, string? projectId, HttpContext ctx, CancellationToken ct) =>
        {
            var store = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx2 = projectContexts.Find(projectId);
                if (ctx2 is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx2.Issues;
            }
            string? newParentId = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("parentIssueId", out var pEl))
                    newParentId = pEl.GetString();
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON body" }); }
            if (string.IsNullOrWhiteSpace(newParentId))
                return Results.BadRequest(new { error = "parentIssueId required" });
            try
            {
                if (await store.GetAsync(id, ct) is null)
                    return Results.NotFound(new { error = "task not found", id });
                await store.ReparentAsync(id, newParentId, ct);
                logger.LogInformation("POST /api/tasks/{Id}/reparent: parent -> {Parent}", id, newParentId);
                return Results.Json(new { taskId = id, parentIssueId = newParentId });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Operator strike reset (2026-07-31): the full nudge for a
        // stuck task — clears EVERY strike counter (rework rounds,
        // no-progress rounds, auto-resume budget, review rounds) plus
        // the recorded verdict (so the head gets a fresh review, not
        // an instant re-trip on the stale RequestChanges) and the
        // blockedKind marker, restarts the stale window, and requeues
        // Failed/Blocked to Pending. Unlike /requeue this also
        // de-arms the review + auto-resume bookkeeping that would
        // otherwise re-fire within one sweep. Audited via
        // strikeResetCount.
        app.MapPost("/api/tasks/{id}/close", async (string id, CloseTaskRequest? body, string? projectId, CancellationToken ct) =>
        {
            // Operator close-obsolete (2026-08-01): retire a task
            // outright — work already on main via another task,
            // superseded, won't-fix. Optionally closes the linked PR
            // unmerged (the common case for obsolete PR-carrying
            // tasks). Reported to the machine as OperatorClosed so
            // the state record ends at Closed, not a stale Failed.
            var store = issues;
            var pid = projectId;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx2 = projectContexts.Find(projectId);
                if (ctx2 is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx2.Issues;
            }
            try
            {
                var t = await store.GetAsync(id, ct);
                if (t is null) return Results.NotFound(new { error = "task not found", id });
                if (t.Status is IssueStatus.Completed or IssueStatus.Closed)
                    return Results.Conflict(new { error = $"task is already terminal ({t.Status})" });

                var reason = string.IsNullOrWhiteSpace(body?.Reason)
                    ? "operator closed"
                    : body!.Reason!;
                await store.TransitionAsync(id, IssueStatus.Closed,
                    $"operator close: {reason}",
                    new Dictionary<string, object> { ["clearanceAction"] = "operator-close" }, ct);
                if (lifecycle is not null)
                {
                    var closed = await store.GetAsync(id, ct);
                    if (closed is not null)
                        await lifecycle.ReportAsync(store, closed, Forge.Core.TaskEvent.OperatorClosed,
                            watch: null, hasActiveDevRun: false, ct);
                }

                bool? prClosed = null;
                string? prCloseError = null;
                if (body?.ClosePr == true && t.GetMetadata("prNumber") is { } prText
                    && int.TryParse(prText, out var prNumber))
                {
                    var gh = pid is not null ? gitHubForProject?.Invoke(pid) : gitHubForProject?.Invoke("");
                    if (gh is null)
                    {
                        prCloseError = "no GitHub service resolvable for this project";
                    }
                    else
                    {
                        try
                        {
                            await gh.ClosePullRequestAsync(prNumber, ct);
                            prClosed = true;
                            logger.LogInformation("POST /api/tasks/{Id}/close: PR #{Pr} closed unmerged ({Reason})", id, prNumber, reason);
                        }
                        catch (Exception ex)
                        {
                            prCloseError = ex.Message;
                            logger.LogWarning(ex, "POST /api/tasks/{Id}/close: PR #{Pr} close failed (task closed regardless)", id, prNumber);
                        }
                    }
                }

                logger.LogInformation("POST /api/tasks/{Id}/close: task closed ({Reason}, prClosed={PrClosed})", id, reason, prClosed);
                return Results.Json(new { taskId = id, status = "Closed", reason, prClosed, prCloseError });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/close failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/park", async (string id, CloseTaskRequest? body, string? projectId, CancellationToken ct) =>
        {
            // Operator park: hold a LIVE task (Blocked) without closing
            // it — the opposite of close-obsolete. The watch sweep only
            // polls Pending|InProgress, so a parked task leaves the
            // review/rework loop until the operator requeues it. An
            // in-flight run that finishes after the park is swallowed by
            // the CommitPushPr stale-dispatch guard (never pushes).
            var store = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx2 = projectContexts.Find(projectId);
                if (ctx2 is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx2.Issues;
            }
            try
            {
                var t = await store.GetAsync(id, ct);
                if (t is null) return Results.NotFound(new { error = "task not found", id });
                if (t.Status is IssueStatus.Completed or IssueStatus.Closed)
                    return Results.Conflict(new { error = $"task is already terminal ({t.Status})" });
                if (t.Status is IssueStatus.Blocked)
                    return Results.Conflict(new { error = "task is already Blocked" });

                var reason = string.IsNullOrWhiteSpace(body?.Reason)
                    ? "operator parked"
                    : body!.Reason!;
                await store.TransitionAsync(id, IssueStatus.Blocked,
                    $"operator park: {reason}",
                    new Dictionary<string, object> { ["blockedKind"] = "operator-park", ["parkReason"] = reason }, ct);
                if (lifecycle is not null)
                {
                    var parked = await store.GetAsync(id, ct);
                    if (parked is not null)
                        await lifecycle.ReportAsync(store, parked, Forge.Core.TaskEvent.OperatorBlocked,
                            watch: null, hasActiveDevRun: false, ct);
                }
                logger.LogInformation("POST /api/tasks/{Id}/park: task parked ({Reason})", id, reason);
                return Results.Json(new { taskId = id, status = "Blocked", reason });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/park failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/reset-strikes", async (string id, string? projectId, HttpContext ctx, CancellationToken ct) =>
        {
            var store = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx2 = projectContexts.Find(projectId);
                if (ctx2 is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx2.Issues;
            }
            try
            {
                var t = await store.GetAsync(id, ct);
                if (t is null) return Results.NotFound(new { error = "task not found", id });
                if (t.Status is not (IssueStatus.Failed or IssueStatus.Blocked or IssueStatus.InProgress))
                    return Results.Conflict(new { error = $"only Failed, Blocked, or InProgress tasks can have strikes reset (status is {t.Status})" });

                var resets = int.TryParse(t.GetMetadata("strikeResetCount"), out var n) ? n + 1 : 1;
                var meta = new Dictionary<string, object>
                {
                    ["retryCount"] = null!,
                    ["reworkAttempts"] = null!,
                    ["reworkForSha"] = null!,
                    ["reworkReason"] = null!,
                    ["reworkContext"] = null!,
                    ["noProgressAttempts"] = null!,
                    ["autoResumeAttempts"] = null!,
                    ["reviewRound"] = null!,
                    ["reviewVerdict"] = null!,
                    ["reviewSha"] = null!,
                    ["reviewNotes"] = null!,
                    ["blockedKind"] = null!,
                    ["lastError"] = null!,
                    ["lastErrorAt"] = null!,
                    ["strikeResetCount"] = resets.ToString(),
                };
                if (t.GetMetadata("prNumber") is not null)
                    meta["prOpenedAt"] = DateTime.UtcNow.ToString("O");
                // Failed/Blocked requeue to Pending (dispatch re-claims
                // or the watch re-adopts); InProgress stays — the watch
                // owns it, the cleared verdict triggers a fresh review.
                var to = t.Status is IssueStatus.Failed or IssueStatus.Blocked
                    ? IssueStatus.Pending
                    : IssueStatus.InProgress;
                // Failure-ledger clearance marker: stamped on BOTH
                // paths. Failed/Blocked→Pending crosses the status
                // boundary and publishes from the choke point; the
                // InProgress case crosses no boundary, so the choke
                // point keys the clearance signal off this metadata
                // stamp instead (phase-1 C4 edge — the open ledger row
                // would otherwise stay un-actioned). clearanceActionAt
                // is the per-gesture nonce: only a FRESH stamp signals,
                // so the stamp surviving in metadata can't re-signal on
                // unrelated later transitions.
                meta["clearanceAction"] = "operator-reset-strikes";
                meta["clearanceActionAt"] = DateTime.UtcNow.ToString("O");
                if (to == IssueStatus.Pending)
                {
                    meta["requeuedFromFailedAt"] = DateTime.UtcNow.ToString("O");
                }
                await store.TransitionAsync(id, to,
                    $"operator strike reset #{resets} (rework/review/no-progress/auto-resume strikes cleared)",
                    meta, ct);
                // Same lifecycle repair as /requeue: without the
                // machine report the state metadata stays Failed/
                // BlockedOperator and the next dispatch violates
                // (state stuck, board contradicts itself).
                if (to == IssueStatus.Pending && lifecycle is not null)
                {
                    var requeued = await store.GetAsync(id, ct);
                    if (requeued is not null)
                        await lifecycle.ReportAsync(store, requeued, Forge.Core.TaskEvent.OperatorRequeue,
                            watch: null, hasActiveDevRun: false, ct);
                }
                logger.LogInformation("POST /api/tasks/{Id}/reset-strikes: {From} -> {To}, all strike counters cleared (reset #{N})",
                    id, t.Status, to, resets);
                return Results.Json(new { taskId = id, status = to.ToString(), strikeResetCount = resets });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/reset-strikes failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/adopt-pr", async (string id, AdoptPrRequest? body, string? projectId, HttpContext http, CancellationToken ct) =>
        {
            // Explicit operator recovery. The task IS the watch; never create a pr-watch row.
            var context = projectId is null ? null : projectContexts?.Find(projectId);
            if (projectId is not null && context is null)
                return Results.NotFound(new { error = "project not found", projectId });
            var store = context?.Issues ?? issues;
            var pid = context?.Options.Id ?? (store as IssueStore)?.ProjectId ?? "";
            try
            {
                if (body is null || body.PrNumber <= 0 || string.IsNullOrWhiteSpace(body.Branch))
                    return Results.BadRequest(new { error = "prNumber (> 0) and branch are required" });
                var gh = gitHubForProject?.Invoke(pid);
                if (gh is null || lifecycle is null)
                    return Results.Problem(detail: "PR validation and task lifecycle services are required", statusCode: 503);
                var t = await store.GetAsync(id, ct);
                if (t is null) return Results.NotFound(new { error = "task not found", id });
                if (t.Status is not (IssueStatus.Failed or IssueStatus.Blocked))
                    return Results.Conflict(new { error = "park the task before adopting a PR; only Failed or Blocked work can be adopted" });
                if ((t.GetMetadata("prNumber") is { Length: > 0 } existingPr && existingPr != body.PrNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
                    (t.GetMetadata("branch") is { Length: > 0 } existingBranch && existingBranch != body.Branch) ||
                    (body.WorktreePath is not null && body.WorktreePath != t.GetMetadata("worktreePath")))
                    return Results.Conflict(new { error = "adoption cannot rebind an existing PR, branch or worktree path" });
                var current = Enum.TryParse<TaskLifecycleState>(t.GetMetadata("state"), out var recorded)
                    ? recorded : TaskStateProjector.Derive(t, null, false, DateTime.UtcNow).State;
                if (!TaskStateMachine.IsLegal(TaskEvent.OperatorAdoptPr, current, TaskLifecycleState.PROpen))
                    return Results.Conflict(new { error = "task lifecycle cannot adopt a PR" });
                var runStore = store is IssueStore concrete ? new AgentRunStore(concrete.Db) : runs;
                if (runStore is null)
                    return Results.Problem(detail: "Active-run validation is required", statusCode: 503);
                if ((hasActiveWorker is not null && await hasActiveWorker(pid, id, ct)) ||
                    (await runStore.ListActiveAsync(ct)).Any(r => r.TaskId == id && r.Role is "CoreDev" or "ClientDev"))
                    return Results.Conflict(new { error = "engineering work is still active" });

                var pr = await gh.GetPullRequestAsync(body.PrNumber, ct);
                var repository = $"{gh.Owner}/{gh.Repo}";
                if (context is not null && (!Uri.TryCreate(context.Options.RepoUrl, UriKind.Absolute, out var repoUri) ||
                    !string.Equals(repoUri.AbsolutePath.Trim('/').Replace(".git", "", StringComparison.Ordinal), repository, StringComparison.OrdinalIgnoreCase)))
                    return Results.Conflict(new { error = "GitHub service does not match the task project" });
                if (pr.Number != body.PrNumber || pr.State.Value != Octokit.ItemState.Open || pr.Merged ||
                    !string.Equals(pr.Base?.Repository?.FullName, repository, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(pr.Head?.Repository?.FullName, repository, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(pr.Head?.Ref, body.Branch, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(pr.Head?.Sha) ||
                    (context is not null && !string.Equals(pr.Base?.Ref, context.Options.DefaultBranch, StringComparison.Ordinal)) ||
                    (body.ExpectedHeadSha is not null && !string.Equals(pr.Head?.Sha, body.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase)))
                    return Results.Conflict(new { error = "PR must be open, unmerged, and match the project, branch, base and expected head" });

                // Refuse a changed task after the external lookup rather than revive a newly completed run.
                var fresh = await store.GetAsync(id, ct);
                if (fresh is null || fresh != t)
                    return Results.Conflict(new { error = "task changed during PR validation; retry adoption" });
                var now = DateTime.UtcNow.ToString("O");
                var metadata = new Dictionary<string, object>
                {
                    ["prNumber"] = pr.Number, ["branch"] = pr.Head!.Ref,
                    ["prUrl"] = pr.HtmlUrl, ["prOpenedAt"] = now,
                    ["operatorAdoptedAt"] = now, ["operatorAdoptedBy"] = http.User.Identity?.Name ?? "operator",
                    ["operatorAdoptedFromStatus"] = t.Status.ToString(), ["operatorAdoptedFromState"] = current.ToString(),
                    ["operatorAdoptedHeadSha"] = pr.Head.Sha, ["branchSha"] = pr.Head.Sha,
                    ["clearanceAction"] = "operator-adopt-pr", ["clearanceActionAt"] = now,
                };
                // Approval belongs to a PR at an exact head. Archive mismatched evidence before
                // clearing its live fields, so a newly attached orphan cannot inherit approval.
                if (t.GetMetadata("prNumber") != pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                    !string.Equals(t.GetMetadata("reviewSha"), pr.Head.Sha, StringComparison.OrdinalIgnoreCase))
                {
                    var reviewKeys = new[] { "reviewSha", "reviewVerdict", "reviewNotes", "reviewRound", "reviewStartedAt" };
                    metadata["operatorAdoptedPriorReview"] = JsonSerializer.Serialize(reviewKeys.ToDictionary(key => key, t.GetMetadata));
                    foreach (var key in reviewKeys) metadata[key] = null!;
                }
                if (body.WorktreePath is not null) metadata["worktreePath"] = body.WorktreePath;
                // Merge metadata only: exact-head review, strikes and historical failure/violation evidence survive.
                // Keep Failed/Blocked until the observed PR and legal lifecycle record have been persisted.
                await lifecycle.ReportAsync(store, t, TaskEvent.OperatorAdoptPr, null, false, ct, metadata);
                await store.AddEventAsync(id, "operator.adopt_pr", JsonSerializer.Serialize(metadata), ct);
                await store.SetCheckpointAsync(id, DispatchCheckpoint.PrOpened, ct);
                await store.TransitionAsync(id, IssueStatus.InProgress, error: null, ct: ct);
                logger.LogInformation("Operator adopted PR #{Pr} on task {TaskId} in project {ProjectId}", pr.Number, id, pid);
                return Results.Json(new { taskId = id, prNumber = pr.Number, headSha = pr.Head.Sha });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/adopt-pr failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/recover", async (string id, string? projectId, CancellationToken ct) =>
        {
            if (recovery is null) return Results.Problem(detail: "StartupRecovery not configured", statusCode: 503);
            try
            {
                // Ids are per-project sequences — the existence check
                // must run against the OWNING project's store.
                var store = issues;
                if (projectId is not null && projectContexts is not null)
                {
                    var pctx = projectContexts.Find(projectId);
                    if (pctx is null) return Results.NotFound(new { error = "project not found", projectId });
                    store = pctx.Issues;
                }
                if (await store.GetAsync(id, ct) is null)
                    return Results.NotFound(new { error = "task not found", id });
                var reportId = await recovery.RunAsync(specId: null, ct: ct);
                return Results.Json(new { taskId = id, reportId });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/recover failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });
    }

    public sealed record AdoptPrRequest(int PrNumber, string Branch, string? WorktreePath, string? ExpectedHeadSha = null);

    /// <summary>Body for POST /api/tasks/{id}/close: the operator's
    /// reason (audit trail) and whether to also close the linked PR
    /// unmerged.</summary>
    public sealed record CloseTaskRequest(string? Reason, bool ClosePr);

    private static Dictionary<string, object?> ParseMetadata(string? metadataJson)
    {
        var d = new Dictionary<string, object?>();
        if (string.IsNullOrEmpty(metadataJson)) return d;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return d;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                d[p.Name] = p.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => p.Value.GetString(),
                    System.Text.Json.JsonValueKind.Number => p.Value.GetRawText(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Null => null,
                    _ => p.Value.GetRawText(),
                };
            }
        }
        catch { }
        return d;
    }

    private static string? ExtractMeta(string? metadataJson, string key)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString();
        }
        catch { }
        return null;
    }
}
