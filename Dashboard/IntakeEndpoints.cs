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
        ILogger logger)
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
                return Results.BadRequest(new { error = ex.Message });
            }
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
        questions = m.Questions?.Select(q =>
        {
            var withDefaults = q.WithYesNoDefault();
            return new { question = withDefaults.Question, options = withDefaults.Options };
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
}
