using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Forge.Agents;
using Forge.Core;
using Forge.Orchestrator.Slots;
using Forge.Projects;

namespace Forge.Dashboard;

/// <summary>
/// The Agents control surface: for each engineering role — identity
/// (name, type, territory, tools), the FULL role prompt (with source:
/// project override vs built-in), the effective provider+model (with
/// source: live override vs llm.roles vs default), the concurrency
/// slot meter, and what the role is doing RIGHT NOW (active run with
/// heartbeat) plus its last finished run.
///
/// <para>
/// The model is editable live: PUT writes a DB-backed override
/// (<see cref="RoleModelOverrides"/>) that the chat client factory
/// and run registry consult per run — no restart. DELETE clears it,
/// falling back to appsettings resolution.
/// </para>
/// </summary>
public static class AgentsEndpoints
{
    public static void MapAgentsEndpoints(
        WebApplication app,
        RoleAgentRegistry registry,
        LlmConfig? llmConfig,
        RoleModelOverrides? overrides,
        SlotTable? slots,
        AgentRunStore? runs,
        ProjectContextFactory? projectFactory)
    {
        app.MapGet("/api/agents/roles", async (string? projectId, CancellationToken ct) =>
        {
            var pid = string.IsNullOrWhiteSpace(projectId) ? "forge" : projectId;
            string? projectRoot = null;
            IReadOnlyDictionary<string, int> projectRoles = new Dictionary<string, int>();
            var ctx = projectFactory?.Find(pid);
            if (ctx is not null)
            {
                projectRoot = ctx.Options.Root;
                projectRoles = ctx.Options.Roles;
            }

            var active = runs is not null ? await runs.ListActiveAsync(ct) : Array.Empty<AgentRunStore.AgentRunRecord>();
            var roles = new List<object>();
            foreach (var (agentType, role) in registry.All())
            {
                // Model: live override > llm.roles > provider default.
                object model;
                if (llmConfig is null)
                {
                    model = new { provider = (string?)"?", model = (string?)"?", source = "unknown" };
                }
                else
                {
                    var (p, m, isOverride) = llmConfig.ResolveEffective(agentType, overrides);
                    var source = isOverride ? "override"
                        : llmConfig.Roles.ContainsKey(agentType) ? "role-config"
                        : "default";
                    model = new { provider = (string?)p.Name, model = (string?)m, source };
                }

                // Prompt: per-project agents/ dir wins, else built-ins.
                var (promptSource, promptPath, promptContent) = LoadPrompt(projectRoot, role.AgentName);

                var activeRun = active.FirstOrDefault(r =>
                    string.Equals(r.Role, agentType.ToString(), StringComparison.OrdinalIgnoreCase));
                var lastRun = runs is not null
                    ? (await runs.ListRecentAsync(limit: 1, role: agentType.ToString(), ct: ct)).FirstOrDefault()
                    : null;

                var slotMax = slots?.MaxFor(pid, role.AgentName) ?? 0;
                if (slotMax == 0)
                    slotMax = Configuration.DefaultProjectRoles.MaxFor(
                        new Dictionary<string, int>(projectRoles, StringComparer.OrdinalIgnoreCase), role.AgentName);

                roles.Add(new
                {
                    name = role.AgentName,
                    agentType = agentType.ToString(),
                    territory = role.ProjectSubdir,
                    tools = role.AllowedTools,
                    model,
                    slot = new { inFlight = slots?.InFlight(pid, role.AgentName) ?? 0, max = slotMax },
                    prompt = new { source = promptSource, path = promptPath, content = promptContent },
                    currentRun = activeRun is null ? null : new
                    {
                        id = activeRun.Id,
                        taskId = activeRun.TaskId,
                        startedAt = activeRun.StartedAt,
                        lastActivityAt = activeRun.LastActivityAt,
                        messageCount = activeRun.MessageCount,
                        toolCallCount = activeRun.ToolCallCount,
                    },
                    lastRun = lastRun is null ? null : new
                    {
                        id = lastRun.Id,
                        taskId = lastRun.TaskId,
                        status = lastRun.Status,
                        finishedAt = lastRun.FinishedAt,
                        durationMs = lastRun.DurationMs,
                        error = lastRun.Error,
                    },
                });
            }

            return Results.Ok(new
            {
                roles,
                providers = llmConfig?.Providers.Select(p => p.Name).ToArray() ?? Array.Empty<string>(),
                overridesEditable = overrides is not null,
            });
        });

        app.MapPut("/api/agents/roles/{name}/model", async (string name, PutRoleModelRequest? body, CancellationToken ct) =>
        {
            if (overrides is null || llmConfig is null)
                return Results.Json(new { error = "model overrides are not available in this mode" }, statusCode: 503);
            if (body is null || string.IsNullOrWhiteSpace(body.Provider) || string.IsNullOrWhiteSpace(body.Model))
                return Results.BadRequest(new { error = "provider and model are required" });
            var role = registry.ByAgentName(name);
            if (role is null)
                return Results.NotFound(new { error = $"unknown role '{name}'" });
            if (llmConfig.Providers.All(p => !string.Equals(p.Name, body.Provider, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = $"provider '{body.Provider}' is not configured; known: {string.Join(", ", llmConfig.Providers.Select(p => p.Name))}" });

            var agentType = registry.TypeOf(role);
            await overrides.SetAsync(agentType, body.Provider, body.Model, ct);
            return Results.Ok(new { role = name, provider = body.Provider, model = body.Model, source = "override" });
        });

        app.MapDelete("/api/agents/roles/{name}/model", async (string name, CancellationToken ct) =>
        {
            if (overrides is null)
                return Results.Json(new { error = "model overrides are not available in this mode" }, statusCode: 503);
            var role = registry.ByAgentName(name);
            if (role is null)
                return Results.NotFound(new { error = $"unknown role '{name}'" });
            await overrides.ClearAsync(registry.TypeOf(role), ct);
            return Results.Ok(new { role = name, source = "role-config-or-default" });
        });
    }

    private static (string Source, string? Path, string? Content) LoadPrompt(string? projectRoot, string agentName)
    {
        try
        {
            var dir = RolePromptRoot.Resolve(projectRoot ?? Directory.GetCurrentDirectory());
            var source = projectRoot is not null && Directory.Exists(Path.Combine(projectRoot, "agents"))
                ? "project"
                : "builtin";
            var path = Path.Combine(dir, $"{agentName}.md");
            return File.Exists(path)
                ? (source, path, File.ReadAllText(path))
                : (source, path, null);
        }
        catch
        {
            return ("missing", null, null);
        }
    }

    public sealed record PutRoleModelRequest(string Provider, string Model);
}
