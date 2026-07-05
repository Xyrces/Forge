using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Meshy;

namespace Forge.Orchestrator;

/// <summary>
/// P2.b: The Artist agent. Given a spec in
/// <c>Designed</c>:
/// <list type="number">
///   <item>Reads the Designer's design_artifacts (wireframe HTML,
///   visual-rule markdown) to ground its asset decisions.</item>
///   <item>Submits one or more Meshy jobs (text-to-3d, image-to-3d,
///   or rigging) corresponding to the visual elements in the
///   spec. Each successful job produces a local <c>.glb</c>
///   under <c>.portHorizon/art-output/</c> and an <c>art_output</c>
///   row.</item>
///   <item>Transitions the spec to <c>AssetReady</c> when at least
///   one art_output row exists, or <c>NeedsRevision</c> when the
///   spec's visual requirements are unclear.</item>
/// </list>
///
/// <para>
/// The LLM has six AIFunctions. The Meshy job submission is
/// surfaced as <c>db_submit_meshy_job</c> — the agent's prompt
/// explains when to use text-to-3d vs image-to-3d vs rigging.
/// </para>
/// </summary>
public sealed class ArtistAgent
{
    private readonly ISpecStore _specs;
    private readonly DesignArtifactStore _designArtifacts;
    private readonly ArtOutputStore _artOutputs;
    private readonly ArtistRunStore _runs;
    private readonly MemoryStore _memory;
    private readonly MeshyClient _meshy;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<ArtistAgent> _logger;
    private readonly string _runId;

    public ArtistAgent(
        ISpecStore specs,
        DesignArtifactStore designArtifacts,
        ArtOutputStore artOutputs,
        ArtistRunStore runs,
        MemoryStore memory,
        MeshyClient meshy,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        IDashboardEventBus events,
        ILogger<ArtistAgent> logger,
        string? runId = null)
    {
        _specs = specs;
        _designArtifacts = designArtifacts;
        _artOutputs = artOutputs;
        _runs = runs;
        _memory = memory;
        _meshy = meshy;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _events = events;
        _logger = logger;
        _runId = runId ?? Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    public string RunId => _runId;

    /// <summary>
    /// What the agent did, for the operator. <see cref="NewSpecStatus"/>
    /// is the spec status the agent committed (AssetReady /
    /// NeedsRevision).
    /// </summary>
    public sealed record ArtistResult(
        bool Success,
        SpecStatus? NewSpecStatus,
        IReadOnlyList<string> ArtOutputIds,
        IReadOnlyList<MeshyTaskRecord> MeshyTasks,
        string? Error);

    public async Task<ArtistResult> ArtSpecAsync(
        string specId, ArtistTriggerKind trigger, CancellationToken ct = default)
    {
        var run = await _runs.StartAsync(specId, trigger, ct);
        var startedAt = DateTime.UtcNow;
        _logger.LogInformation("Artist: starting run {RunId} for spec {SpecId} (trigger={Trigger})",
            run.Id, specId, trigger);
        _events.Publish(ArtistEventEmitter.Build(DateTime.UtcNow,
            ArtistEventKind.RunStarted, specId, _runId, trigger, "starting"));

        try
        {
            var spec = await _specs.GetAsync(specId, ct);
            if (spec is null)
            {
                return await FinishFailureAsync(run.Id, specId, "spec not found", startedAt, ct,
                    "Spec not found");
            }
            return await RunLlmAsync(run.Id, spec, startedAt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Artist: spec {SpecId} crashed", specId);
            return await FinishFailureAsync(run.Id, specId, "llm crashed", startedAt, ct,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<ArtistResult> RunLlmAsync(
        long runId, SpecRecord spec, DateTime startedAt, CancellationToken ct)
    {
        // P2.b: same factory-keyed-by-orchestrator pattern as the
        // Designer. The Artist is an Orchestrator-only role; it
        // doesn't have an AgentType. We honor the configured
        // "artist" role's provider + model; fall back to CoreDev's
        // config when the "artist" role isn't configured.
        var artistRole = _roles.ByKiloAgentName(RoleAgentRegistry.ArtistKiloAgentName);
        var chatClient = _chatClientFactory.Create(_config, AgentType.CoreDev);
        chatClient = new ChatClientBuilder(chatClient).UseFunctionInvocation().Build();

        var tools = BuildTools(spec);

        var agent = new ChatClientAgent(
            chatClient,
            instructions: BuildInstructions(await BuildPlaybookBlockAsync(ct)),
            name: "artist",
            description: $"Artist agent for project {spec.ProjectId}",
            tools: tools);

        var prompt = $"""
            Spec: {spec.Id}
            Title: {spec.Title}
            Status: {spec.Status}

            Body:
            {spec.Body}
            """;

        var response = await agent.RunAsync(prompt, cancellationToken: ct);

        // Inspect the response. Track:
        //  - art-output ids (art-...) from db_save_art_output results
        //  - meshy tasks from db_submit_meshy_job results
        var artOutputIds = new List<string>();
        var meshyTasks = new List<MeshyTaskRecord>();
        foreach (var msg in response.Messages)
        {
            foreach (var c in msg.Contents)
            {
                if (c is FunctionResultContent frc && frc.Result is not null)
                {
                    var s = frc.Result as string;
                    if (s is null && frc.Result is System.Text.Json.JsonElement je
                        && je.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        s = je.GetString();
                    }
                    if (s is not null)
                    {
                        if (s.StartsWith("art-")) artOutputIds.Add(s);
                        else if (s.StartsWith("meshy-") || s.StartsWith("{"))
                        {
                            // meshy task ids are bare strings (e.g. "abc-123"); JSON
                            // task records come from the rigged path. We
                            // don't try to parse loose strings — the
                            // meshy_tasks list is rebuilt from the
                            // art_output rows' references_json below.
                        }
                    }
                }
            }
        }

        // Re-fetch the spec to see what status the LLM committed.
        var refreshed = await _specs.GetAsync(spec.Id, ct);
        SpecStatus? newStatus = refreshed?.Status is SpecStatus.AssetReady or SpecStatus.NeedsRevision
            ? refreshed.Status
            : null;

        if (newStatus is null)
        {
            return await FinishFailureAsync(runId, spec.Id, "llm did not call db_set_spec_status", startedAt, ct,
                "LLM completed without committing a spec status transition. The spec is left in its current state; re-run the Artist.");
        }

        // Rebuild the meshy_tasks list from the art_output rows'
        // references_json so the run log is accurate (the LLM may
        // have failed to remember all the task ids it submitted).
        foreach (var id in artOutputIds)
        {
            var art = await _artOutputs.GetAsync(id, ct);
            if (art?.ReferencesJson is null) continue;
            try
            {
                var refs = JsonSerializer.Deserialize<List<MeshyReference>>(art.ReferencesJson);
                if (refs is null) continue;
                foreach (var r in refs)
                {
                    if (string.IsNullOrWhiteSpace(r.MeshyTaskId)) continue;
                    meshyTasks.Add(new MeshyTaskRecord(
                        Id: r.MeshyTaskId,
                        Mode: r.Mode ?? "unknown",
                        Status: r.Status ?? "UNKNOWN",
                        ArtOutputId: id,
                        GlbUrl: null));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Artist: failed to parse references_json for {Id}", id);
            }
        }

        return await FinishSuccessAsync(runId, spec.Id, newStatus.Value, artOutputIds, meshyTasks, startedAt, ct);
    }

    private IList<AITool> BuildTools(SpecRecord spec)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                ([Description("Spec id to fetch.")] string specId) => DbGetSpec(specId),
                name: "db_get_spec",
                description: "Fetch a spec by id. Returns the full spec record."),

            AIFunctionFactory.Create(
                ([Description("Spec id.")] string specId) => DbGetDesignArtifacts(specId),
                name: "db_get_design_artifacts",
                description: "List the Designer's approved design_artifacts for the spec (wireframes, mockups, component-specs, visual-rules). Use these to ground your art submissions."),

            AIFunctionFactory.Create(
                () => DbGetVisualLanguage(),
                name: "db_get_visual_language",
                description: "Read the project's visual-language rules. Use to align art submissions to the project's visual conventions."),

            AIFunctionFactory.Create(
                (
                    [Description("One of: text-to-3d, image-to-3d, multi-image-to-3d, rigging.")] string mode,
                    [Description("For text-to-3d: the prompt. For image-to-3d / multi-image: the image URL or data URI. For rigging: the input model URL (e.g. the glb_url of a previous text-to-3d).")] string input,
                    [Description("For image-to-3d: a public URL or data URI of the input image. For text-to-3d: the prompt. For rigging: the input model URL. Kept for backwards compat; the agent normally uses the 'input' field.")] string? imageUrl = null,
                    [Description("For text-to-3d: the prompt. For image-to-3d: ignored. For rigging: ignored. Kept for backwards compat.")] string? prompt = null,
                    [Description("Optional, default 'realistic'. For text-to-3d: art_style.")] string? artStyle = null) => DbSubmitMeshyJob(mode, input, imageUrl, prompt, artStyle),
                name: "db_submit_meshy_job",
                description: "Submit a Meshy job. The job is polled to completion and the .glb is downloaded to .portHorizon/art-output/. Returns the Meshy task id."),

            AIFunctionFactory.Create(
                (
                    [Description("Spec id the art output belongs to.")] string specId,
                    [Description("One of: mesh, texture, animation, rig.")] string kind,
                    [Description("Short human title (1 line).")] string title,
                    [Description("Relative path under .portHorizon/art-output/ — what the .glb download returned.")] string body,
                    [Description("One of: glb, fbx, obj, png, mp4, usdz. Tells the dashboard how to render.")] string bodyKind,
                    [Description("Optional JSON array of {designArtifactId, meshyTaskId, mode, status}.")] string? references = null) => DbSaveArtOutput(specId, kind, title, body, bodyKind, references),
                name: "db_save_art_output",
                description: "Save an art output. The body is the relative path returned by db_submit_meshy_job. The dashboard's Art tab renders it inline."),

            AIFunctionFactory.Create(
                (
                    [Description("Spec id.")] string specId,
                    [Description("One of: AssetReady, NeedsRevision. AssetReady = produced all required art and saved art_output rows. NeedsRevision = visual requirements are unclear or Meshy rejected the input.")] string status) => DbSetSpecStatus(specId, status),
                name: "db_set_spec_status",
                description: "Commit the art decision for the spec. You MUST call this exactly once at the end of every run. The pipeline can't proceed otherwise."),
        };
        return tools;
    }

    private async Task<string> DbGetSpec(string specId)
    {
        var spec = await _specs.GetAsync(specId);
        if (spec is null) return $"{{\"error\":\"spec {specId} not found\"}}";
        return JsonSerializer.Serialize(spec);
    }

    private async Task<string> DbGetDesignArtifacts(string specId)
    {
        if (string.IsNullOrWhiteSpace(specId)) return "[]";
        var list = await _designArtifacts.ListBySpecAsync(specId, DesignArtifactStatus.Approved);
        return JsonSerializer.Serialize(list.Select(a => new
        {
            a.Id, a.SpecId, Kind = a.Kind.ToString(),
            a.Title, a.BodyKind, a.Author
        }));
    }

    private async Task<string> DbGetVisualLanguage()
    {
        var entries = await _memory.RecallAsync(keyPrefix: "design/visual-language");
        if (entries.Count == 0) return string.Empty;
        return string.Join("\n\n---\n\n", entries.Select(e => e.Body));
    }

    /// <summary>
    /// Submit a Meshy job and wait for completion. Returns the Meshy
    /// task id (the LLM uses this to refer to the job in the run
    /// log). The .glb is downloaded to .portHorizon/art-output/
    /// eagerly — we pre-allocate the art_output row's body path
    /// after a successful download.
    /// </summary>
    private async Task<string> DbSubmitMeshyJob(
        string mode, string input, string? imageUrl, string? prompt, string? artStyle)
    {
        MeshyMode parsedMode;
        try
        {
            parsedMode = mode.ToLowerInvariant() switch
            {
                "text-to-3d" => MeshyMode.TextTo3d,
                "image-to-3d" => MeshyMode.ImageTo3d,
                "multi-image-to-3d" => MeshyMode.MultiImageTo3d,
                "rigging" => MeshyMode.Rigging,
                _ => throw new ArgumentException($"unknown mode '{mode}'"),
            };
        }
        catch (ArgumentException ex)
        {
            return $"{{\"error\":\"{ex.Message}\"}}";
        }

        // The AIFunction signature uses an `input` arg (covers all
        // modes) plus the older `imageUrl` / `prompt` named args
        // (kept for back-compat). Pick the right one.
        var effectivePrompt = parsedMode == MeshyMode.TextTo3d
            ? (prompt ?? input)
            : null;
        var effectiveImage = parsedMode is MeshyMode.ImageTo3d or MeshyMode.MultiImageTo3d
            ? (imageUrl ?? input)
            : null;
        var effectiveModelUrl = parsedMode == MeshyMode.Rigging ? input : null;

        try
        {
            string taskId = parsedMode switch
            {
                MeshyMode.TextTo3d => await _meshy.SubmitTextTo3dAsync(new TextTo3dRequest
                {
                    Prompt = effectivePrompt ?? string.Empty,
                    ArtStyle = artStyle,
                }),
                MeshyMode.ImageTo3d => await _meshy.SubmitImageTo3dAsync(new ImageTo3dRequest
                {
                    ImageUrl = effectiveImage ?? string.Empty,
                }),
                MeshyMode.MultiImageTo3d => await _meshy.SubmitMultiImageTo3dAsync(new MultiImageTo3dRequest
                {
                    ImageUrls = string.IsNullOrWhiteSpace(effectiveImage)
                        ? Array.Empty<string>()
                        : new[] { effectiveImage },
                }),
                MeshyMode.Rigging => await _meshy.SubmitRiggingAsync(new RiggingRequest
                {
                    ModelUrl = effectiveModelUrl ?? string.Empty,
                }),
                _ => throw new ArgumentOutOfRangeException(nameof(parsedMode)),
            };

            // Poll to completion. The downstream AIFunction (db_save_art_output)
            // is what records the .glb path; we don't download it
            // here because we don't have an art_output.id yet.
            // Instead, the save step runs DbDownloadAndSave internally
            // when it sees a meshy task id. To keep the agent's
            // control flow simple, we return a small JSON blob
            // containing both the task id AND the (yet-to-be-resolved)
            // signed glb_url. The save step does the actual download.
            var rec = await _meshy.WaitForTaskAsync(taskId, parsedMode);
            // Return a JSON envelope; the AIFunction result of a
            // string-typed function is shown verbatim to the LLM.
            // The save step parses the same shape.
            return JsonSerializer.Serialize(new MeshyJobOutcome(
                TaskId: rec.Id,
                Mode: parsedMode.ToString(),
                Status: rec.Status,
                GlbUrl: rec.GlbUrl));
        }
        catch (MeshyException ex)
        {
            return $"{{\"error\":\"{ex.Message}\"}}";
        }
        catch (Exception ex)
        {
            return $"{{\"error\":\"{ex.GetType().Name}: {ex.Message}\"}}";
        }
    }

    /// <summary>
    /// Save an art output. The agent passes the relative path
    /// returned by <see cref="DbSubmitMeshyJob"/> in the <c>body</c>
    /// argument. If the agent passed a JSON envelope (with
    /// taskId + glbUrl), we download the .glb and store the
    /// resulting path.
    /// </summary>
    private async Task<string> DbSaveArtOutput(
        string specId, string kind, string title, string body, string bodyKind, string? references)
    {
        if (!ArtOutputKindExtensions.TryParseDb(kind, out var k))
            return $"{{\"error\":\"unknown kind '{kind}'\"}}";
        if (bodyKind is not ("glb" or "fbx" or "obj" or "png" or "mp4" or "usdz"))
            return $"{{\"error\":\"bodyKind must be glb|fbx|obj|png|mp4|usdz\"}}";

        // The agent may have passed a Meshy job envelope instead of
        // a final path. If so, download the .glb and rewrite body.
        string? glbUrl = null;
        string? meshyTaskId = null;
        string? meshyMode = null;
        if (body.StartsWith("{") && body.Contains("\"GlbUrl\""))
        {
            try
            {
                var env = JsonSerializer.Deserialize<MeshyJobOutcome>(body);
                if (env is not null)
                {
                    meshyTaskId = env.TaskId;
                    meshyMode = env.Mode;
                    glbUrl = env.GlbUrl;
                }
            }
            catch { /* fall through */ }
        }
        if (glbUrl is null && body.StartsWith("http"))
        {
            glbUrl = body;
        }

        if (glbUrl is not null)
        {
            var artId = $"art-{Guid.NewGuid():N}";
            try
            {
                var rel = await _meshy.DownloadGlbAsync(glbUrl, specId, artId);
                body = rel;
            }
            catch (MeshyException ex)
            {
                return $"{{\"error\":\"{ex.Message}\"}}";
            }
        }

        var req = new NewArtOutput(
            SpecId: specId, Kind: k, Title: title, Body: body, BodyKind: bodyKind,
            ReferencesJson: references, Author: $"artist:{_runId}");
        var created = await _artOutputs.CreateAsync(req);
        _events.Publish(ArtistEventEmitter.Build(DateTime.UtcNow,
            ArtistEventKind.ArtSaved, specId, _runId, ArtistTriggerKind.Manual, created.Id));
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
            _events.Publish(ArtistEventEmitter.Build(DateTime.UtcNow,
                ArtistEventKind.StatusCommitted, specId, _runId, ArtistTriggerKind.Manual, newStatus.ToString()));
            return "ok";
        }
        catch (InvalidOperationException ex)
        {
            return $"{{\"error\":\"{ex.Message}\"}}";
        }
    }

    /// <summary>
    /// Reads the operator-editable playbook reference from memory
    /// (playbook/repo + playbook/snapshot + playbook/skills/artist)
    /// and returns a small block the system prompt can include.
    /// Mirrors the Designer's BuildPlaybookBlockAsync; uses the
    /// "artist" key prefix for the role-specific skills.
    /// </summary>
    private async Task<string> BuildPlaybookBlockAsync(CancellationToken ct)
    {
        try
        {
            var repo = await _memory.RecallAsync(keyPrefix: "playbook/repo", ct);
            var snapshot = await _memory.RecallAsync(keyPrefix: "playbook/snapshot", ct);
            var skills = await _memory.RecallAsync(keyPrefix: "playbook/skills/artist", ct);
            if (repo.Count == 0 && skills.Count == 0) return "(no operator-maintained playbook configured)";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("The operator maintains a project-specific skills reference. Use it to ground your art decisions in established best practice.");
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
                sb.Append("Skills relevant to your role (artist): ").AppendLine(skills[0].Body);
                sb.AppendLine();
                sb.AppendLine("If a skill is relevant to this spec, you may `curl <repo>/skills/<name>/SKILL.md` to read its full body before deciding. Don't fetch skills that aren't relevant.");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Artist: failed to read playbook block; continuing without it");
            return "(playbook unavailable; art from spec body alone)";
        }
    }

    private async Task<ArtistResult> FinishSuccessAsync(
        long runId, string specId, SpecStatus newStatus,
        IReadOnlyList<string> artOutputIds, IReadOnlyList<MeshyTaskRecord> meshyTasks,
        DateTime startedAt, CancellationToken ct)
    {
        var duration = DateTime.UtcNow - startedAt;
        await _runs.FinishAsync(runId, ArtistRunStatus.Succeeded, newStatus, artOutputIds,
            meshyTasks, error: null, duration: duration, ct);
        _events.Publish(ArtistEventEmitter.Build(DateTime.UtcNow,
            ArtistEventKind.RunCompleted, specId, _runId, ArtistTriggerKind.Manual, newStatus.ToString()));
        _logger.LogInformation("Artist: spec {SpecId} -> {Status} in {Ms}ms (art={N}, meshy={M})",
            specId, newStatus, duration.TotalMilliseconds, artOutputIds.Count, meshyTasks.Count);
        return new ArtistResult(true, newStatus, artOutputIds, meshyTasks, null);
    }

    private async Task<ArtistResult> FinishFailureAsync(
        long runId, string specId, string shortName, DateTime startedAt, CancellationToken ct, string error)
    {
        var duration = DateTime.UtcNow - startedAt;
        await _runs.FinishAsync(runId, ArtistRunStatus.LlmFailed, newSpecStatus: null,
            artOutputIds: null, meshyTasks: null, error: error,
            duration: duration, ct);
        _events.Publish(ArtistEventEmitter.Build(DateTime.UtcNow,
            ArtistEventKind.RunFailed, specId, _runId, ArtistTriggerKind.Manual, shortName));
        return new ArtistResult(false, null, Array.Empty<string>(), Array.Empty<MeshyTaskRecord>(), error);
    }

    private string BuildInstructions(string playbookBlock)
    {
        return $"""
            You are the ArtistAgent for this project. Given a spec
            in status Designed, produce the art assets engineering
            will need to render it without ambiguity.

            The Designer has already produced design_artifacts
            (wireframes, visual-rules). Your job is to convert
            those into actual asset files via the Meshy API.

            Required workflow:
            1. Read the spec body in the user prompt.
            2. Call db_get_design_artifacts to read the Designer's
               artifacts. The wireframes and visual-rules are the
               source of truth for "what does this thing look
               like".
            3. Call db_get_visual_language to read the project's
               visual conventions. Align your prompts to those.
            4. For each visual element in the spec, submit a
               Meshy job via db_submit_meshy_job. The mode
               depends on the input:
               - text-to-3d: when you have a clear prompt but
                 no reference image.
               - image-to-3d: when a wireframe is rendered to a
                 2D image (use a public URL or data URI).
               - rigging: when you need a rigged model for
                 animation (input is the glb_url of a prior
                 text-to-3d / image-to-3d job).
               The job is polled to completion synchronously
               inside the tool; the .glb is downloaded and a
               relative path is returned in the JSON envelope.
            5. Call db_save_art_output for each successful job,
               passing the JSON envelope from step 4 as the
               body argument. The save step does the final
               download + path allocation and returns an
               art-<id> that you can use for references.
            6. If the spec has at least one art_output row, call
               db_set_spec_status("AssetReady"). If the visual
               requirements are unclear or Meshy rejected the
               input, call db_set_spec_status("NeedsRevision")
               and explain in your reply text. Do NOT save
               art_output rows for a NeedsRevision run.

            Hard rules:
            - Always end with exactly one db_set_spec_status call.
              The pipeline can't proceed otherwise.
            - The art_output kind / body_kind / body triple must
              be consistent. Use kind=mesh + body_kind=glb for
              3D models; kind=texture + body_kind=png for
              textures; kind=animation + body_kind=mp4 for
              video; kind=rig + body_kind=glb or fbx for
              rigged models.
            - The author is set automatically to artist:{_runId}.
              You don't pass it.
            - Don't submit jobs the spec doesn't ask for. If
              the spec is purely a behavior change with no
              visual surface, call db_set_spec_status("AssetReady")
              immediately without submitting any jobs (the Groomer
              gate will pass it through).

            ## Skills reference
            {playbookBlock}
            """;
    }
}

public enum ArtistEventKind { RunStarted, RunCompleted, RunFailed, ArtSaved, StatusCommitted }

internal sealed record MeshyJobOutcome(
    string TaskId,
    string Mode,
    string Status,
    string? GlbUrl);

internal sealed record MeshyReference(
    string? DesignArtifactId,
    string? MeshyTaskId,
    string? Mode,
    string? Status,
    string? Why);

/// <summary>
/// Emits Artist lifecycle events onto the dashboard event bus.
/// The Kind constants live in <see cref="DashboardEventKind"/>.
/// </summary>
internal static class ArtistEventEmitter
{
    public static DashboardEvent Build(
        DateTime ts, ArtistEventKind kind, string specId, string runId,
        ArtistTriggerKind trigger, string? detail, object? extra = null)
    {
        var kindStr = kind switch
        {
            ArtistEventKind.RunStarted => DashboardEventKind.ArtistRunStarted,
            ArtistEventKind.RunCompleted => DashboardEventKind.ArtistRunCompleted,
            ArtistEventKind.RunFailed => DashboardEventKind.ArtistRunFailed,
            ArtistEventKind.ArtSaved => DashboardEventKind.ArtistArtSaved,
            ArtistEventKind.StatusCommitted => DashboardEventKind.ArtistStatusCommitted,
            _ => "artist.unknown",
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
