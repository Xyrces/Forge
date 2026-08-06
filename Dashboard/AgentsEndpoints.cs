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
        ProjectContextFactory? projectFactory,
        Agents.ProviderApiKeyResolver? apiKeys = null)
    {
        app.MapGet("/api/agents/roles", async (string? projectId, CancellationToken ct) =>
        {
            // No hardcoded project id: absent param = the PRIMARY
            // project (first registered), never a literal id — a
            // registry whose first project isn't "forge" must not
            // silently load a nonexistent/forge context.
            var pid = string.IsNullOrWhiteSpace(projectId)
                ? projectFactory?.KnownProjects.FirstOrDefault()?.Id
                : projectId;
            string? projectRoot = null;
            IReadOnlyDictionary<string, int> projectRoles = new Dictionary<string, int>();
            var ctx = pid is null ? null : projectFactory?.Find(pid);
            // Slot accounting needs a concrete project id; an
            // unresolved project reports 0 in-flight and falls back
            // to the default caps below.
            var slotPid = pid ?? string.Empty;
            if (ctx is not null)
            {
                projectRoot = ctx.Options.Root;
                projectRoles = ctx.Options.Roles;
            }

            // agent_run is per-project workload data (the runner
            // writes to the owning project's schema) — the run panels
            // read the LENS project's store. Rows pre-dating the
            // per-project writers live in the primary store.
            var runStore = runs is not null && ctx is not null
                ? new AgentRunStore(((Core.IssueStore)ctx.Issues).Db)
                : runs;
            var active = runStore is not null ? await runStore.ListActiveAsync(ct) : Array.Empty<AgentRunStore.AgentRunRecord>();
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
                    var (p, m, isOverride) = llmConfig.ResolveEffective(agentType, overrides, pid);
                    var source = isOverride
                        ? (overrides?.GetScope(agentType, pid) == "project" ? "override (project)" : "override (global)")
                        : llmConfig.Roles.ContainsKey(agentType) ? "config"
                        : "default";
                    model = new { provider = (string?)p.Name, model = (string?)m, source };
                }

                // Prompt: per-project agents/ dir wins, else built-ins.
                var (promptSource, promptPath, promptContent) = LoadPrompt(projectRoot, role.AgentName);

                var activeRun = active.FirstOrDefault(r =>
                    string.Equals(r.Role, agentType.ToString(), StringComparison.OrdinalIgnoreCase));
                var lastRun = runStore is not null
                    ? (await runStore.ListRecentAsync(limit: 1, role: agentType.ToString(), ct: ct)).FirstOrDefault()
                    : null;

                var slotMax = slots?.MaxFor(slotPid, role.AgentName) ?? 0;
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
                    slot = new { inFlight = slots?.InFlight(slotPid, role.AgentName) ?? 0, max = slotMax },
                    prompt = new { source = promptSource, path = promptPath, content = promptContent },
                    currentRun = activeRun is null ? null : new
                    {
                        id = activeRun.Id,
                        taskId = activeRun.TaskId,
                        projectId = activeRun.ProjectId,
                        startedAt = activeRun.StartedAt,
                        lastActivityAt = activeRun.LastActivityAt,
                        messageCount = activeRun.MessageCount,
                        toolCallCount = activeRun.ToolCallCount,
                        phase = activeRun.Phase,
                        resumedSession = activeRun.ResumedSession,
                    },
                    lastRun = lastRun is null ? null : new
                    {
                        id = lastRun.Id,
                        taskId = lastRun.TaskId,
                        projectId = lastRun.ProjectId,
                        status = lastRun.Status,
                        finishedAt = lastRun.FinishedAt,
                        durationMs = lastRun.DurationMs,
                        error = lastRun.Error,
                    },
                });
            }

            // Pipeline (scheduler-side) roles — the same catalog the
            // project drill-down's slot grid renders, so both surfaces
            // answer "what agents exist?" identically. Model column:
            // intake resolves its own AgentType; designer/groomer/
            // artist borrow coredev's client (shown, not separately
            // editable); the orchestrator runs no LLM.
            var pipeline = new List<object>();
            foreach (var pr in RoleAgentRegistry.Pipeline)
            {
                object pModel;
                if (llmConfig is null)
                {
                    pModel = new { provider = (string?)"?", model = (string?)"?", source = "unknown" };
                }
                else if (pr.ModelType is { } mt)
                {
                    var (p, m, isOverride) = llmConfig.ResolveEffective(mt, overrides, pid);
                    var source = isOverride
                        ? (overrides?.GetScope(mt, pid) == "project" ? "override (project)" : "override (global)")
                        : llmConfig.Roles.ContainsKey(mt) ? "config"
                        : "default";
                    pModel = new { provider = (string?)p.Name, model = (string?)m, source };
                }
                else if (pr.InheritsModelFrom is not null)
                {
                    var (p, m, _) = llmConfig.ResolveEffective(Core.AgentType.CoreDev, overrides, pid);
                    pModel = new { provider = (string?)p.Name, model = (string?)m, source = $"inherits {pr.InheritsModelFrom}" };
                }
                else
                {
                    pModel = new { provider = (string?)null, model = (string?)null, source = "none" };
                }

                var pSlotMax = slots?.MaxFor(slotPid, pr.AgentName) ?? 0;
                if (pSlotMax == 0)
                    pSlotMax = Configuration.DefaultProjectRoles.MaxFor(
                        new Dictionary<string, int>(projectRoles, StringComparer.OrdinalIgnoreCase), pr.AgentName);

                pipeline.Add(new
                {
                    name = pr.AgentName,
                    description = pr.Description,
                    surface = pr.Surface,
                    model = pModel,
                    modelEditable = pr.ModelType is not null,
                    slot = new { inFlight = slots?.InFlight(slotPid, pr.AgentName) ?? 0, max = pSlotMax },
                });
            }

            return Results.Ok(new
            {
                roles,
                pipeline,
                providers = llmConfig?.Providers.Select(p => p.Name).ToArray() ?? Array.Empty<string>(),
                overridesEditable = overrides is not null,
            });
        });

        app.MapGet("/api/agents/providers/{name}/models", async (string name, CancellationToken ct) =>
        {
            if (llmConfig is null)
                return Results.Json(new { error = "no LLM config in this mode" }, statusCode: 503);
            var provider = llmConfig.Providers.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                return Results.NotFound(new { error = $"unknown provider '{name}'" });
            // Substitute the DB-resolved key (Secrets page) for the
            // config placeholder — the catalog call must authenticate
            // with the same key the runs use.
            var effective = apiKeys?.Get(name) is { Length: > 0 } resolved
                ? provider with { ApiKey = resolved }
                : provider;
            var models = await ProviderModelCatalog.GetModelsAsync(effective, ct);
            return (IResult)Results.Ok(new { provider = provider.Name, models, fetchError = ProviderModelCatalog.LastError(provider.Name) });
        });

        app.MapPut("/api/agents/roles/{name}/model", async (string name, PutRoleModelRequest? body, CancellationToken ct) =>
        {
            if (overrides is null || llmConfig is null)
                return Results.Json(new { error = "model overrides are not available in this mode" }, statusCode: 503);
            if (body is null || string.IsNullOrWhiteSpace(body.Provider) || string.IsNullOrWhiteSpace(body.Model))
                return Results.BadRequest(new { error = "provider and model are required" });
            var agentType = ResolveAgentType(registry, name);
            if (agentType is null)
                return Results.NotFound(new { error = $"unknown role '{name}'" });
            if (llmConfig.Providers.All(p => !string.Equals(p.Name, body.Provider, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = $"provider '{body.Provider}' is not configured; known: {string.Join(", ", llmConfig.Providers.Select(p => p.Name))}" });

            // Project-scoped by default (operator rule 2026-07-30:
            // an override set for one project must never leak into
            // another's runs); explicit null projectId = global.
            await overrides.SetAsync(agentType.Value, body.Provider, body.Model, ct, projectId: body.ProjectId);
            return Results.Ok(new { role = name, provider = body.Provider, model = body.Model, source = "override" });
        });

        app.MapDelete("/api/agents/roles/{name}/model", async (string name, string? projectId, CancellationToken ct) =>
        {
            if (overrides is null)
                return Results.Json(new { error = "model overrides are not available in this mode" }, statusCode: 503);
            var agentType = ResolveAgentType(registry, name);
            if (agentType is null)
                return Results.NotFound(new { error = $"unknown role '{name}'" });
            await overrides.ClearAsync(agentType.Value, ct, projectId: projectId);
            return Results.Ok(new { role = name, projectId, source = "config-or-default" });
        });
    }

    /// <summary>Role name → AgentType for the override APIs: the four
    /// engineering roles via the registry, plus intake (the only
    /// pipeline role with its own AgentType + model).</summary>
    private static Core.AgentType? ResolveAgentType(RoleAgentRegistry registry, string name)
    {
        var role = registry.ByAgentName(name);
        if (role is not null) return registry.TypeOf(role);
        if (string.Equals(name, "intake", StringComparison.OrdinalIgnoreCase)) return Core.AgentType.Intake;
        return null;
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

    public sealed record PutRoleModelRequest(string Provider, string Model, string? ProjectId);
}
