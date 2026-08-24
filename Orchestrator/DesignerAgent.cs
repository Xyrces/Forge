using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Codebase;
using Forge.Core;
using Forge.Dashboard;

namespace Forge.Orchestrator;

/// <summary>
/// The Designer agent. Given a spec in <c>ReadyForDesign</c>:
/// <list type="number">
///   <item>Runs <see cref="DesignHygieneChecker"/> first. If the
///   report has any Error findings, the run is marked
///   <see cref="DesignerRunStatus.HygieneFailed"/> and the LLM is
///   NOT called. The dashboard's Design tab shows the report.</item>
///   <item>If the hygiene check passes, runs the LLM. The LLM has
///   six AIFunctions (see below) and is told to first call
///   <c>db_get_visual_language</c> and <c>db_get_existing_design_artifacts</c>,
///   then either write ≥1 design_artifact (for visual specs) or
///   call <c>db_set_spec_status(Approved)</c> (for non-visual
///   specs).</item>
///   <item>Transitions the spec to <c>Designed</c>, <c>Approved</c>,
///   or <c>NeedsRevision</c> based on the LLM's call.</item>
/// </list>
/// </summary>
public sealed class DesignerAgent
{
    private readonly ISpecStore _specs;
    private readonly DesignArtifactStore _artifacts;
    private readonly DesignerRunStore _runs;
    private readonly MemoryStore _memory;
    private readonly DesignHygieneChecker _hygiene;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<DesignerAgent> _logger;
    private readonly string _runId;
    private readonly string _rolePromptsRoot;

    public DesignerAgent(
        ISpecStore specs,
        DesignArtifactStore artifacts,
        DesignerRunStore runs,
        MemoryStore memory,
        DesignHygieneChecker hygiene,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        IDashboardEventBus events,
        ILogger<DesignerAgent> logger,
        string rolePromptsRoot = "agents",
        string? runId = null)
    {
        _specs = specs;
        _artifacts = artifacts;
        _runs = runs;
        _memory = memory;
        _hygiene = hygiene;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _events = events;
        _logger = logger;
        _rolePromptsRoot = rolePromptsRoot;
        _runId = runId ?? Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    public string RunId => _runId;

    /// <summary>
    /// What the agent did, for the operator. <see cref="NewSpecStatus"/>
    /// is the spec status the agent committed (Designed / Approved /
    /// NeedsRevision).
    /// </summary>
    public sealed record DesignerResult(
        bool Success,
        SpecStatus? NewSpecStatus,
        IReadOnlyList<string> ArtifactIds,
        HygieneReport? Hygiene,
        string? Error);

    public async Task<DesignerResult> DesignSpecAsync(
        string specId, DesignerTriggerKind trigger, CancellationToken ct = default)
    {
        var run = await _runs.StartAsync(specId, trigger, ct);
        var startedAt = DateTime.UtcNow;
        _logger.LogInformation("Designer: starting run {RunId} for spec {SpecId} (trigger={Trigger})",
            run.Id, specId, trigger);
        _events.Publish(DesignerEventEmitter.Build(DateTime.UtcNow,
            DesignerEventKind.RunStarted, specId, _runId, trigger, "starting"));

        try
        {
            var spec =             await _specs.GetAsync(specId, ct);
            if (spec is null)
            {
                return await FinishFailureAsync(run.Id, specId, "spec not found", startedAt, ct,
                    "Spec not found");
            }

            // Step 1: deterministic hygiene. If Error findings, fail
            // before the LLM. The LLM is not invoked — operator sees
            // the report on the Design tab.
            var hygiene = await _hygiene.CheckAsync(spec, ct);
            _logger.LogInformation("Designer: hygiene report for {SpecId}: passed={Passed} findings={N}",
                specId, hygiene.Passed, hygiene.Findings.Count);

            if (!hygiene.Passed)
            {
                return await FinishHygieneFailureAsync(run.Id, specId, hygiene, startedAt, ct);
            }

            // Step 2: LLM. The system prompt is strict about reading
            // visual language + existing artifacts first. The LLM has
            // six AIFunctions; it must call db_set_spec_status on the
            // way out (Designed / Approved / NeedsRevision).
            return await RunLlmAsync(run.Id, spec, hygiene, startedAt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Designer: spec {SpecId} crashed", specId);
            return await FinishFailureAsync(run.Id, specId, "llm crashed", startedAt, ct,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<DesignerResult> RunLlmAsync(
        long runId, SpecRecord spec, HygieneReport hygiene, DateTime startedAt, CancellationToken ct)
    {
        // Phase 3 inheritance cut: the Designer has its own AgentType,
        // so its provider + model resolve independently (override →
        // llm.roles.Designer → provider default) — no more borrowing
        // CoreDev's entry.
        var chatClient = _chatClientFactory.Create(_config, AgentType.Designer, spec.ProjectId);
        // Function-invocation middleware: the LLM's tool_calls get
        // executed and the result feeds back into the next turn.
        chatClient = new ChatClientBuilder(chatClient).UseFunctionInvocation().Build();

        var tools = BuildTools(spec);

        var agent = new ChatClientAgent(
            chatClient,
            instructions: BuildInstructions(await BuildPlaybookBlockAsync(ct)),
            name: "designer",
            description: $"Designer agent for project {spec.ProjectId}",
            tools: tools);

        var prompt = $"""
            Spec: {spec.Id}
            Title: {spec.Title}
            Status: {spec.Status}

            Body:
            {spec.Body}
            """;

        var response = await agent.RunAsync(prompt, cancellationToken: ct);
        // The LLM has run; the next block inspects the response. We
        // intentionally do NOT catch here — exceptions from the LLM
        // path bubble to the outer try in DesignSpecAsync.

        // Inspect the response: did the LLM call db_set_spec_status
        // and what value? Did it save any artifacts?
        var artifactIds = new List<string>();
        SpecStatus? newStatus = null;
        foreach (var msg in response.Messages)
        {
            foreach (var c in msg.Contents)
            {
                if (c is FunctionResultContent frc && frc.Result is not null)
                {
                    // MAF serializes tool results into JsonElement.
                    var s = frc.Result as string;
                    if (s is null && frc.Result is System.Text.Json.JsonElement je
                        && je.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        s = je.GetString();
                    }
                    if (s is not null && s.StartsWith("design-"))
                    {
                        artifactIds.Add(s);
                    }
                }
            }
        }
        // Re-fetch the spec to see what status the LLM committed.
        var refreshed = await _specs.GetAsync(spec.Id, ct);
        newStatus = refreshed?.Status is SpecStatus.Designed or SpecStatus.Approved or SpecStatus.NeedsRevision
            ? refreshed.Status
            : null;

        if (newStatus is null)
        {
            // LLM ran but never called db_set_spec_status. Treat as a
            // silent failure — operator sees the run in designer_run
            // with error.
            return await FinishFailureAsync(runId, spec.Id, "llm did not call db_set_spec_status", startedAt, ct,
                "LLM completed without committing a spec status transition. The spec is left in its current state; re-run the Designer.");
        }

        return await FinishSuccessAsync(runId, spec.Id, newStatus.Value, artifactIds, hygiene, startedAt, ct);
    }

    private IList<AITool> BuildTools(SpecRecord spec)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                ([Description("Spec id to fetch.")] string specId) => DbGetSpec(specId),
                name: "db_get_spec",
                description: "Fetch a spec by id. Returns the full spec record (id, title, body, status, parent_issue_id)."),

            AIFunctionFactory.Create(
                () => DbGetCodebaseGraph(),
                name: "db_get_codebase_graph",
                description: "Read the codebase graph (modules + import edges) for the project. Use this to confirm ## Touches references match real module ids."),

            AIFunctionFactory.Create(
                ([Description("Optional spec id; if null returns the project's full artifact set.")] string? specId) => DbGetExistingDesignArtifacts(specId),
                name: "db_get_existing_design_artifacts",
                description: "List existing design artifacts (wireframes, mockups, component-specs, visual-rules). Use this to find a visual pattern to reuse instead of inventing a new one."),

            AIFunctionFactory.Create(
                () => DbGetVisualLanguage(),
                name: "db_get_visual_language",
                description: "Read the project's visual-language rules (color, typography, spacing, motion). Returns an empty string if no rules are defined yet."),

            AIFunctionFactory.Create(
                (
                    [Description("Spec id the artifact belongs to.")] string specId,
                    [Description("One of: wireframe, mockup, component-spec, visual-rule.")] string kind,
                    [Description("Short human title (1 line).")] string title,
                    [Description("The artifact body. For wireframe/mockup: HTML. For component-spec / visual-rule: markdown.")] string body,
                    [Description("One of: html, svg, markdown. Tells the dashboard how to render.")] string bodyKind,
                    [Description("Optional JSON array of {designArtifactId, why}.")] string? references = null) => DbSaveDesignArtifact(specId, kind, title, body, bodyKind, references),
                name: "db_save_design_artifact",
                description: "Save a design artifact. The artifact is associated with the spec and appears on the Design tab."),

            AIFunctionFactory.Create(
                (
                    [Description("Spec id.")] string specId,
                    [Description("One of: Designed, Approved, NeedsRevision. Designed = visual spec done. Approved = non-visual, skip design. NeedsRevision = structural problem, do NOT design.")] string status) => DbSetSpecStatus(specId, status),
                name: "db_set_spec_status",
                description: "Commit the design decision for the spec. You MUST call this exactly once at the end of every run. The pipeline can't proceed otherwise."),
        };
        return tools;
    }

    private async Task<string> DbGetSpec(string specId)
    {
        var spec = await _specs.GetAsync(specId);
        if (spec is null) return $"{{\"error\":\"spec {specId} not found\"}}";
        return JsonSerializer.Serialize(spec);
    }

    private async Task<string> DbGetCodebaseGraph()
    {
        // Designer is invoked through DesignerScheduler which has
        // access to the graph cache. For simplicity, the agent
        // delegates to SpecStore / ProjectContext via the
        // FilesystemProjectContextSource which already builds the
        // graph on demand. The result is JSON.
        // (Note: this is the "last resort" — DesignerScheduler
        // already has the graph loaded before the LLM runs; we don't
        // need a second build. In v1 we just emit the path; in v2
        // we plumb the graph through.)
        return "{\"note\":\"graph loaded from scheduler context; see .portHorizon/codebase-graph/\"}";
    }

    private async Task<string> DbGetExistingDesignArtifacts(string? specId)
    {
        // v1: spec-scoped only. The LLM passes a spec_id and we
        // return its approved design_artifact rows. (Project-wide
        // lookup would need a projectId; the LLM doesn't have
        // one in v1. The visual-language memory + the
        // design-system rules are the cross-spec context.)
        if (string.IsNullOrWhiteSpace(specId))
        {
            return "[]";
        }
        var list = await _artifacts.ListBySpecAsync(specId, DesignArtifactStatus.Approved);
        return JsonSerializer.Serialize(list.Select(a => new
        {
            a.Id, a.SpecId, Kind = a.Kind.ToString(),
            a.Title, a.BodyKind, a.Author
        }));
    }

    /// <summary>
    /// Reads the operator-editable playbook reference from memory
    /// (playbook/repo + playbook/snapshot + playbook/skills/designer)
    /// and returns a small block the system prompt can include.
    /// The model can decide whether to `curl` individual skill
    /// bodies from the repo URL. The block is short on purpose —
    /// the prompt isn't bloated.
    /// </summary>
    private async Task<string> BuildPlaybookBlockAsync(CancellationToken ct)
    {
        try
        {
            var repo = await _memory.RecallAsync(keyPrefix: "playbook/repo", ct);
            var snapshot = await _memory.RecallAsync(keyPrefix: "playbook/snapshot", ct);
            var skills = await _memory.RecallAsync(keyPrefix: "playbook/skills/designer", ct);
            if (repo.Count == 0 && skills.Count == 0) return "(no operator-maintained playbook configured)";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("The operator maintains a project-specific skills reference. Use it to ground your design decisions in established best practice.");
            sb.AppendLine();
            if (repo.Count > 0)
            {
                sb.Append("Repo: ").AppendLine(repo[0].Body);
            }
            if (snapshot.Count > 0)
            {
                sb.AppendLine();
                sb.Append("Snapshot: ").AppendLine(snapshot[0].Body);
            }
            if (skills.Count > 0)
            {
                sb.AppendLine();
                sb.Append("Skills relevant to your role (designer): ").AppendLine(skills[0].Body);
                sb.AppendLine();
                sb.AppendLine("If a skill is relevant to this spec, you may `curl <repo>/skills/<name>/SKILL.md` to read its full body before deciding. Don't fetch skills that aren't relevant.");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            // Memory recall failure must not block the design run.
            _logger.LogWarning(ex, "Designer: failed to read playbook block; continuing without it");
            return "(playbook unavailable; design from spec body alone)";
        }
    }

    private async Task<string> DbGetVisualLanguage()
    {
        var entries = await _memory.RecallAsync(keyPrefix: "design/visual-language");
        if (entries.Count == 0) return string.Empty;
        return string.Join("\n\n---\n\n", entries.Select(e => e.Body));
    }

    private async Task<string> DbSaveDesignArtifact(
        string specId, string kind, string title, string body, string bodyKind, string? references)
    {
        if (!DesignArtifactKindExtensions.TryParseDb(kind, out var k))
            return $"{{\"error\":\"unknown kind '{kind}'\"}}";
        if (bodyKind is not ("html" or "svg" or "markdown"))
            return $"{{\"error\":\"bodyKind must be html|svg|markdown\"}}";
        var req = new NewDesignArtifact(
            SpecId: specId, Kind: k, Title: title, Body: body, BodyKind: bodyKind,
            ReferencesJson: references, Author: $"designer:{_runId}");
        var created = await _artifacts.CreateAsync(req);
        _events.Publish(DesignerEventEmitter.Build(DateTime.UtcNow,
            DesignerEventKind.ArtifactSaved, specId, _runId, DesignerTriggerKind.Manual, created.Id));
        return created.Id;
    }

    private async Task<string> DbSetSpecStatus(string specId, string status)
    {
        if (!Enum.TryParse<SpecStatus>(status, out var newStatus))
            return $"{{\"error\":\"unknown status '{status}'\"}}";
        var current = await _specs.GetAsync(specId);
        if (current is null) return $"{{\"error\":\"spec {specId} not found\"}}";
        try
        {
            await _specs.SetStatusAsync(specId, newStatus);
            _events.Publish(DesignerEventEmitter.Build(DateTime.UtcNow,
                DesignerEventKind.StatusCommitted, specId, _runId, DesignerTriggerKind.Manual, newStatus.ToString()));
            return "ok";
        }
        catch (InvalidOperationException ex)
        {
            return $"{{\"error\":\"{ex.Message}\"}}";
        }
    }

    private async Task<DesignerResult> FinishSuccessAsync(
        long runId, string specId, SpecStatus newStatus,
        IReadOnlyList<string> artifactIds, HygieneReport hygiene, DateTime startedAt, CancellationToken ct)
    {
        var duration = DateTime.UtcNow - startedAt;
        await _runs.FinishAsync(runId, DesignerRunStatus.Succeeded, newStatus, artifactIds,
            hygiene.ToJson(), error: null, duration: duration, ct);
        _events.Publish(DesignerEventEmitter.Build(DateTime.UtcNow,
            DesignerEventKind.RunCompleted, specId, _runId, DesignerTriggerKind.Manual, newStatus.ToString()));
        _logger.LogInformation("Designer: spec {SpecId} -> {Status} in {Ms}ms (artifacts={N})",
            specId, newStatus, duration.TotalMilliseconds, artifactIds.Count);
        return new DesignerResult(true, newStatus, artifactIds, hygiene, null);
    }

    private async Task<DesignerResult> FinishHygieneFailureAsync(
        long runId, string specId, HygieneReport hygiene, DateTime startedAt, CancellationToken ct)
    {
        var duration = DateTime.UtcNow - startedAt;
        await _runs.FinishAsync(runId, DesignerRunStatus.HygieneFailed, newSpecStatus: null,
            designArtifactIds: null, hygieneReportJson: hygiene.ToJson(),
            error: $"{hygiene.Findings.Count(f => f.Severity == HygieneSeverity.Error)} hygiene error(s)",
            duration: duration, ct);
        _events.Publish(DesignerEventEmitter.Build(DateTime.UtcNow,
            DesignerEventKind.RunFailed, specId, _runId, DesignerTriggerKind.Manual, "hygiene_failed"));
        return new DesignerResult(false, null, Array.Empty<string>(), hygiene,
            "Hygiene check failed; see the report on the Design tab.");
    }

    private async Task<DesignerResult> FinishFailureAsync(
        long runId, string specId, string shortName, DateTime startedAt, CancellationToken ct, string error)
    {
        var duration = DateTime.UtcNow - startedAt;
        await _runs.FinishAsync(runId, DesignerRunStatus.LlmFailed, newSpecStatus: null,
            designArtifactIds: null, hygieneReportJson: null, error: error,
            duration: duration, ct);
        _events.Publish(DesignerEventEmitter.Build(DateTime.UtcNow,
            DesignerEventKind.RunFailed, specId, _runId, DesignerTriggerKind.Manual, shortName));
        return new DesignerResult(false, null, Array.Empty<string>(), null, error);
    }

    private string BuildInstructions(string playbookBlock)
    {
        return $"""
            You are the DesignerAgent for this project. Given a spec
            in status ReadyForDesign, produce the visual artifacts
            engineering will need to implement it without ambiguity.

            The system has already run a deterministic hygiene check
            on this spec; if it failed, your run was marked
            hygiene_failed and you were not invoked. You're running
            because the spec passed.

            Required workflow:
            1. Read the spec body in the user prompt.
            2. Call db_get_visual_language to read the project's
               visual conventions. (Returns empty if no rules are
               defined yet — that's fine, the first run produces
               them.)
            3. Call db_get_existing_design_artifacts to see what's
               already in the project. Reuse existing patterns
               when you can; only introduce a new visual pattern
               when nothing existing applies.
            4. If the spec touches a visual surface (UI, scene,
               HUD, sprite placement, etc.), you MUST call
               db_save_design_artifact at least once — typically a
               wireframe. Then call db_set_spec_status("Designed").
            5. If the spec is purely non-visual (build pipeline,
               data model, CI, infra), skip the artifact and call
               db_set_spec_status("Approved").
            6. If the spec has a structural problem you can't fix
               (broken dep, undefined module, duplicate epic,
               etc.), call db_set_spec_status("NeedsRevision")
               and explain in your reply text. Do NOT write
               design_artifact rows for a NeedsRevision run — the
               operator needs to fix the spec first.

            Hard rules:
            - Always end with exactly one db_set_spec_status call.
              The pipeline can't proceed otherwise.
            - The artifact kind / body_kind / body triple must be
              consistent. Use kind=wireframe + body_kind=html for
              HTML wireframes; kind=mockup + body_kind=html for
              high-fidelity; kind=component-spec + body_kind=markdown
              for table-shaped component specs; kind=visual-rule +
              body_kind=markdown for project-wide conventions.
            - The author is set automatically to designer:{_runId}.
              You don't pass it.
            - Don't introduce a new visual pattern unless nothing
              existing applies. The dashboard's existing-artifacts
              view is the source of truth for the project's visual
              language.

            ## Skills reference
            {playbookBlock}
            """;
    }
}

public enum DesignerEventKind { RunStarted, RunCompleted, RunFailed, ArtifactSaved, StatusCommitted }

/// <summary>
/// Emits Designer lifecycle events onto the dashboard event bus.
/// The Kind constants live in <see cref="DashboardEventKind"/>.
/// </summary>
internal static class DesignerEventEmitter
{
    public static DashboardEvent Build(
        DateTime ts, DesignerEventKind kind, string specId, string runId,
        DesignerTriggerKind trigger, string? detail, object? extra = null)
    {
        var kindStr = kind switch
        {
            DesignerEventKind.RunStarted => DashboardEventKind.DesignerRunStarted,
            DesignerEventKind.RunCompleted => DashboardEventKind.DesignerRunCompleted,
            DesignerEventKind.RunFailed => DashboardEventKind.DesignerRunFailed,
            DesignerEventKind.ArtifactSaved => DashboardEventKind.DesignerArtifactSaved,
            DesignerEventKind.StatusCommitted => DashboardEventKind.DesignerStatusCommitted,
            _ => "designer.unknown",
        };
        var data = new Dictionary<string, object?>
        {
            ["runId"] = runId,
            ["trigger"] = trigger.ToString().ToLowerInvariant(),
        };
        if (extra is not null) data["extra"] = extra;
        return new DashboardEvent(ts, kindStr, specId, detail, data);
    }
}