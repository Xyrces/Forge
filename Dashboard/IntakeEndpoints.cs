using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// P1.4 endpoints: intake sessions (list/get/create/send-message/accept-epic).
/// Mounted into the DashboardHost's WebApplication via
/// <see cref="MapIntakeEndpoints"/>.
/// </summary>
public static class IntakeEndpoints
{
    public static void MapIntakeEndpoints(
        WebApplication app,
        IntakeAgentRegistry intakeRegistry,
        IIssueStore issues,
        ISprintStore sprints,
        IIntakeStore intakeStore,
        ILogger logger,
        InitiativeApprovalService? initiatives = null)
    {
        // List intake sessions (optionally scoped to one project —
        // the dashboard's intake page is project-scoped like every
        // other surface; the audit found cross-project sessions
        // indistinguishable in the list).
        app.MapGet("/api/intake/sessions", async (string? project, CancellationToken ct) =>
        {
            var sessions = await intakeStore.ListAsync(ct);
            if (project is not null)
            {
                sessions = sessions
                    .Where(s => string.Equals(s.ProjectId, project, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            return Results.Json(sessions.Select(ToSessionView).ToArray(), DashboardJson.Options);
        });

        // Create a new intake session.
        app.MapPost("/api/intake/sessions", async (HttpContext ctx) =>
        {
            var spec = await JsonSerializer.DeserializeAsync<NewIntakeSessionRequest>(
                ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (spec is null || string.IsNullOrWhiteSpace(spec.ProjectId))
                return Results.BadRequest(new { error = "projectId required" });
            var agent = intakeRegistry.ForProject(spec.ProjectId);
            var session = await agent.StartSessionAsync(spec.Title, ctx.RequestAborted);
            return Results.Json(ToSessionView(session), DashboardJson.Options, statusCode: 201);
        });

        // Get a single session (with messages).
        app.MapGet("/api/intake/sessions/{id}", async (string id, CancellationToken ct) =>
        {
            var session = await intakeStore.GetAsync(id, ct);
            return session is null
                ? Results.NotFound()
                : Results.Json(ToSessionView(session), DashboardJson.Options);
        });

        // Send a user message; runs the LLM, returns the updated session.
        app.MapPost("/api/intake/sessions/{id}/messages", async (string id, HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<SendMessageRequest>(
                ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (body is null || string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "text required" });
            var session = await intakeStore.GetAsync(id, ctx.RequestAborted);
            if (session is null) return Results.NotFound();
            var agent = intakeRegistry.ForProject(session.ProjectId);
            try
            {
                var updated = await agent.SendUserMessageAsync(id, body.Text, ctx.RequestAborted);
                return Results.Json(ToSessionView(updated), DashboardJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Intake send-message failed for session {Id}", id);
                return Results.Json(new { error = ex.Message, sessionId = id },
                    DashboardJson.Options, statusCode: 500);
            }
        });

        // Accept a proposed epic from a specific assistant message.
        app.MapPost("/api/intake/sessions/{id}/accept-epic/{messageId:long}", async (
            string id, long messageId, CancellationToken ct) =>
        {
            var session = await intakeStore.GetAsync(id, ct);
            if (session is null) return Results.NotFound();
            var agent = intakeRegistry.ForProject(session.ProjectId);
            try
            {
                var issue = await agent.AcceptProposedEpicAsync(id, messageId, ct);
                return Results.Json(ToIssueView(issue), DashboardJson.Options);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        app.MapPost("/api/intake/sessions/{id}/review-draft/{messageId:long}", async (
            string id, long messageId, CancellationToken ct) =>
        {
            var session = await intakeStore.GetAsync(id, ct);
            if (session is null) return Results.NotFound();
            try { return Results.Json(await intakeRegistry.ForProject(session.ProjectId).ImportLegacyDraftAsync(id, messageId, ct), DashboardJson.Options); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapGet("/api/intake/proposals", async (string projectId, string? sessionId, string? cursor, CancellationToken ct) =>
        {
            if (initiatives is null) return Results.StatusCode(503);
            var page = await initiatives.Proposals.ListAsync(projectId, ct, limit: 20, afterId: cursor, sessionId: sessionId);
            return Results.Json(new { items = page, nextCursor = page.Count == 20 ? page[^1].Id : null }, DashboardJson.Options);
        });
        app.MapGet("/api/intake/proposals/{id}", async (string id, string projectId, CancellationToken ct) =>
        {
            if (initiatives is null) return Results.StatusCode(503);
            var proposal = await initiatives.Proposals.GetAsync(projectId, id, ct);
            if (proposal is null) return Results.NotFound();
            var ingestion = proposal.ApprovedAt is null ? null : await initiatives.ReceiptAsync(projectId,id,proposal.Revision,ct);
            return Results.Json(new { proposal, ingestion }, DashboardJson.Options);
        });

        app.MapPost("/api/intake/proposals/{id}/expand", async (string id, string projectId, CancellationToken ct) =>
        {
            if (initiatives is null) return Results.StatusCode(503);
            try { return Results.Json(new { sessionId = await initiatives.Proposals.ExpandInIntakeAsync(projectId, id, ct) }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapPost("/api/intake/proposals/{id}/approve", async (string id, string projectId, ApprovalRequest body, CancellationToken ct) =>
        {
            if (initiatives is null) return Results.StatusCode(503);
            if (body.Revision <= 0 || string.IsNullOrWhiteSpace(body.Hash)) return Results.BadRequest(new { error = "Exact revision and content hash required." });
            try { return Results.Json(await initiatives.ApproveAsync(projectId, id, body.Revision, body.Hash, ct), DashboardJson.Options); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapPost("/api/intake/proposals/{id}/retry-refinement", async (string id, string projectId, ApprovalRequest body, CancellationToken ct) =>
        {
            if (initiatives is null) return Results.StatusCode(503);
            var proposal = await initiatives.Proposals.GetAsync(projectId, id, ct);
            if (proposal is null) return Results.NotFound();
            if (proposal.Hash != body.Hash) return Results.Conflict(new { error = "Proposal content changed; reload before retry." });
            return await initiatives.RetryRefinementAsync(projectId, id, body.Revision, ct)
                ? Results.Ok() : Results.Conflict(new { error = "Only an explicitly failed refinement may be retried." });
        });

        // List recent epics proposed via intake (issue.type='epic', assignee='intake').
        app.MapGet("/api/intake/proposed-epics", async (CancellationToken ct) =>
        {
            var all = await issues.ListAsync(new IssueFilter { Assignee = "intake" }, ct);
            return Results.Json(all.Select(ToIssueView).ToArray(), DashboardJson.Options);
        });
    }

    private static object ToSessionView(IntakeSessionRecord s) => new
    {
        id = s.Id,
        projectId = s.ProjectId,
        title = s.Title,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt,
        messages = s.Messages.Select(ToMessageView).ToArray()
    };

    private static object ToMessageView(IntakeMessageRecord m) => new
    {
        id = m.Id,
        sessionId = m.SessionId,
        role = m.Role.ToString(),
        content = m.Content,
        timestamp = m.Timestamp,
        proposedEpicId = m.ProposedEpicId,
        proposedEpicTitle = m.ProposedEpicTitle,
        proposedEpicDescription = m.ProposedEpicDescription,
        questions = m.Questions?.Select(q => new
        {
            header = q.Header,
            question = q.Question,
            multiple = q.Multiple,
            options = q.Options,
        }).ToArray(),
    };

    private static object ToIssueView(IssueRecord t) => new
    {
        id = t.Id,
        type = t.Type,
        title = t.Title,
        description = t.Description,
        status = t.Status.ToString(),
        priority = t.Priority,
        assignee = t.Assignee,
        createdAt = t.CreatedAt,
        updatedAt = t.UpdatedAt,
        closedAt = t.ClosedAt
    };

    private sealed record NewIntakeSessionRequest(string ProjectId, string? Title);
    private sealed record SendMessageRequest(string Text);
    private sealed record ApprovalRequest(int Revision, string Hash);
}
