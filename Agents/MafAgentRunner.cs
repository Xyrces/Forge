using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.AgentTools;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// MAF-based implementation of <see cref="IAgentRunner"/>. Phase 0:
/// wraps <see cref="ChatClientAgent"/>, instantiated fresh per call with
/// the role's instructions loaded from the agents/<role>.md
/// frontmatter <c>description</c> field. Phase 1: skills from
/// <see cref="ISkillSource"/> (global + role-scoped) are appended to the
/// agent's instructions.
///
/// <para>
/// The runner does NOT itself manage worktrees, commits, pushes, or PRs.
/// Those are AIFunctions the agent invokes (P2). Phase 1 still runs the
/// agent in plain text mode (no tools) but the agent now sees the
/// project's skill catalog in its system context.
/// </para>
/// </summary>
public sealed class MafAgentRunner : IAgentRunner
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly ILogger<MafAgentRunner> _logger;
    private readonly string _rolePromptsRoot;
    private readonly Func<string, string?>? _projectRootLookup;
    private readonly Func<string, IReadOnlyDictionary<string, Core.RoleTerritory>?>? _projectTerritoryLookup;
    private readonly Func<string, IReadOnlyList<string>?>? _verifyCommandsLookup;

    /// <summary>Seam for tests: replaces the real verification runner.</summary>
    internal Func<string, IReadOnlyList<string>, ILogger, CancellationToken, Task<AgentTools.RunVerification.Result>>? VerifyRunner { get; set; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _promptRootsByProject = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISkillSource? _skills;
    private readonly MemoryStore? _memory;
    private readonly ContextHandoffStore? _handoffs;
    private readonly Func<DesignArtifactStore?>? _designArtifactsFactory;
    private readonly Func<ISpecStore?>? _specsFactory;
    private readonly Func<ArtOutputStore?>? _artOutputsFactory;
    private readonly ISecretStore? _secrets;
    private readonly IIssueStore? _issues;
    private readonly AgentRunStore? _runs;
    private readonly Func<string?, AgentRunStore?>? _runsByProject;
    private readonly Func<string?, IIssueStore?>? _issueStoreLookup;
    private readonly RoleModelOverrides? _modelOverrides;
    private readonly Configuration.GateOptions _gateOptions;

    /// <summary>
    /// Optional path for the per-run diagnostic side-channel log
    /// (message roles, text lengths, tool-call names). Set by
    /// Program.cs to a file under the Forgesystem state root. When
    /// null the diagnostic write is skipped. Replaces the historical
    /// hardcoded <c>C:\ProgramData\Forge\agent.log</c>, which was
    /// silently swallowed on Linux.
    /// </summary>
    public static string? DiagnosticLogPath { get; set; }

    public MafAgentRunner(
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        ILogger<MafAgentRunner> logger,
        ISkillSource? skills = null,
        string rolePromptsRoot = "agents",
        // Per-project role-prompt resolution (schema v24 era): maps a
        // project id to its clone root so runs load <root>/agents when
        // the project ships its own role prompts. Null/unknown project
        // or no agents dir → the construction-time fallback root.
        Func<string, string?>? projectRootLookup = null,
        // Per-project plan-gate territory: maps a project id to its
        // roles_json $territory overrides. Null / no entry for the role
        // → the registry's built-in territory applies.
        Func<string, IReadOnlyDictionary<string, Core.RoleTerritory>?>? projectTerritoryLookup = null,
        // Per-project pre-push verification commands (roles_json
        // $verify). When set, engineering runs verify their work
        // IN-SESSION before completing — failures feed back to the
        // agent like a plan-gate revision, not a dispatch-level
        // requeue (user direction 2026-07-30: lenient on corrections,
        // don't restart the session).
        Func<string, IReadOnlyList<string>?>? verifyCommandsLookup = null,
        MemoryStore? memory = null,
        ContextHandoffStore? handoffs = null,
        // P5.1 stores — passed as factories so the runner can
        // be constructed before the stores are (avoids a Program.cs
        // re-ordering). The factories are invoked once on first
        // tool build; the result is cached.
        Func<DesignArtifactStore?>? designArtifacts = null,
        Func<ISpecStore?>? specs = null,
        Func<ArtOutputStore?>? artOutputs = null,
        ISecretStore? secrets = null,
        IIssueStore? issues = null,
        AgentRunStore? runs = null,
        // Per-project run registry (operator rule 2026-07-30: run
        // history is workload data and belongs to the OWNING project's
        // schema). Resolves the AgentRunStore for the run's project;
        // the result is used for the WHOLE run (start + heartbeats +
        // finish). Null/unknown project → the construction-time store
        // (legacy single-registry behavior for unattributed runs).
        Func<string?, AgentRunStore?>? runsByProject = null,
        // Per-project follow-up store (operator rule 2026-07-31:
        // file_followup rows are workload data for the RUN's project —
        // the construction-time store is the primary, which stranded
        // porthorizon follow-ups in the forge backlog, where forge's
        // assembler sprinted + dispatched them against the forge repo).
        // Resolves by context projectId; null/unknown → _issues.
        Func<string?, IIssueStore?>? issueStoreLookup = null,
        RoleModelOverrides? modelOverrides = null,
        Configuration.GateOptions? gates = null)
    {
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _logger = logger;
        _skills = skills;
        _rolePromptsRoot = rolePromptsRoot;
        _projectRootLookup = projectRootLookup;
        _projectTerritoryLookup = projectTerritoryLookup;
        _verifyCommandsLookup = verifyCommandsLookup;
        _memory = memory;
        _handoffs = handoffs;
        _designArtifactsFactory = designArtifacts;
        _specsFactory = specs;
        _artOutputsFactory = artOutputs;
        _secrets = secrets;
        _issues = issues;
        _runs = runs;
        _runsByProject = runsByProject;
        _issueStoreLookup = issueStoreLookup;
        _modelOverrides = modelOverrides;
        _gateOptions = gates ?? new Configuration.GateOptions();
    }

public async Task<AgentRunResult> RunAsync(
        AgentType role, string prompt, string? sessionId, CancellationToken ct)
        => await RunAsync(role, prompt, sessionId, context: null, ct);

    public async Task<AgentRunResult> RunAsync(
        AgentType role,
        string prompt,
        string? sessionId,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct)
    {
        var roleDef = _roles.ForType(role);
        var projectId = ResolveContextString(context, "projectId");
        if (string.IsNullOrWhiteSpace(projectId)) projectId = null;
        // The run registry row belongs to the OWNING project's schema:
        // one store for the whole run (start + heartbeats + finish).
        var runStore = _runsByProject?.Invoke(projectId) ?? _runs;
        var roleInstructions = LoadRoleInstructions(roleDef.AgentName, projectId);
        var skillInstructions = _skills is null
            ? string.Empty
            : await BuildSkillInstructionsAsync(role, projectId, ct);
        var memoryInstructions = _memory is null
            ? string.Empty
            : await BuildMemoryInstructionsAsync(context, ct);
        var instructions = string.Join("\n\n", new[]
        {
            roleInstructions,
            skillInstructions,
            BuildSprintBlock(context),
            memoryInstructions,
        }.Where(s => !string.IsNullOrEmpty(s)));
        // P1 fix: instructions go to the agent's instructions: parameter,
        // NOT into the user message. The user prompt is the operator's
        // task text; the system prompt is the role + skills context.
        var fullPrompt = prompt;

        // P3 in progress: surface a real `bash` AIFunction so the model
        // emits structured tool_calls instead of XML fallback. The
        // workingDirectory defaults to the task's worktree if the
        // orchestrator passes one in `context["worktreePath"]`.
        var tools = new List<AITool>();
        var bashWorkingDir = ResolveWorktreePath(context);
        Gates.RunGateState? gateState = null;
        if (!string.IsNullOrWhiteSpace(bashWorkingDir))
        {
            var secretEnv = await ResolveSecretEnvAsync(context, ct);

            // Plan gate (engineering roles): the agent must submit a
            // structured plan and have it approved BEFORE mutating
            // commands work — hard-enforced at the bash tool via a
            // deterministic mutation classifier. Fast-path (mechanical
            // rework rounds) auto-approves on submit.
            Func<bool>? mutationsAllowed = null;
            string? mutationRefusalMessage = null;
            if (role is AgentType.CoreDev or AgentType.ClientDev)
            {
                gateState = new Gates.RunGateState
                {
                    FastPath = string.Equals(
                        ResolveContextString(context, "planFastPath"), "true", StringComparison.OrdinalIgnoreCase),
                };
                var gatePipeline = new Gates.RunGatePipeline(
                    _gateOptions, _memory, name => BuildRunGate(name, projectId), _logger);
                var projectTerritories = projectId is not null && _projectTerritoryLookup is not null
                    ? _projectTerritoryLookup(projectId)
                    : null;
                var (territoryPrefixes, territoryRootFiles) =
                    RoleAgentRegistry.ResolveTerritory(roleDef, projectTerritories);
                var gateContext = new Gates.RunGateContext(
                    TaskId: ResolveContextString(context, "issueId") ?? "unknown",
                    RoleName: roleDef.AgentName,
                    TerritoryPrefixes: territoryPrefixes,
                    TerritoryAllowsRootFiles: territoryRootFiles,
                    WorktreePath: bashWorkingDir,
                    TaskText: prompt.Length > 4000 ? prompt[..4000] : prompt,
                    Plan: "",
                    Ct: ct);
                tools.Add(new AgentTools.SubmitPlanTool(
                    gateState, gatePipeline, gateContext,
                    logger: _logger).AsAIFunction());
                mutationsAllowed = () => gateState.PlanApproved;
            }
            else if (role is AgentType.Reviewer)
            {
                // Reviewer is read-only by policy: it gets the PR
                // worktree for evidence-gathering (the review prompt
                // diff paste is truncated — the reviewer must inspect
                // the branch itself), with mutations hard-refused.
                mutationsAllowed = () => false;
                mutationRefusalMessage =
                    "exit=-1\nstdout:\nstderr: REFUSED — the Reviewer role is read-only. Use read-only commands: git log/diff/show/status/fetch, cat, sed -n, grep, find, ls, dotnet build/test.";
            }

            tools.Add(new BashTool(bashWorkingDir, logger: null, envVars: secretEnv,
                mutationsAllowed: mutationsAllowed, mutationRefusalMessage: mutationRefusalMessage).AsAIFunction());
        }

        // Reviewer drill-in: the dispatcher hands the FULL diff via
        // context (never inlined whole); the pr_diff tool pages it.
        if (role is AgentType.Reviewer
            && context is not null
            && context.TryGetValue("reviewDiff", out var reviewDiffObj)
            && reviewDiffObj is string reviewDiff
            && reviewDiff.Length > 0)
        {
            tools.Add(new AgentTools.PrDiffTool(reviewDiff).AsAIFunction());
        }

        // P5.1 — ArtifactReadTool is always available when the
        // required stores are wired. It lets agents pull a
        // single artifact body on demand rather than have the
        // orchestrator inline every artifact body into every
        // prompt. The tool's read calls are logged to
        // context_handoff for closed-loop debugging.
        var designArtifacts = _designArtifactsFactory?.Invoke();
        var specs = _specsFactory?.Invoke();
        var artOutputs = _artOutputsFactory?.Invoke();
        if (designArtifacts is not null && specs is not null && artOutputs is not null)
        {
            var readTool = new ArtifactReadTool(
                designArtifacts, specs, artOutputs, _handoffs, logger: null);
            tools.Add(readTool.AsAIFunction());
        }

        // Follow-up filing: engineering + review roles can file
        // out-of-scope discoveries as tasks. Filed follow-ups are
        // NOT sprint-eligible — they land parentless with no groomed
        // marker and wait for the groomer's ad-hoc pass (operator
        // rule: no task enters a sprint without technical grooming).
        if (_issues is not null
            && role is AgentType.CoreDev or AgentType.ClientDev or AgentType.QA or AgentType.Reviewer
            && context is not null
            && context.TryGetValue("issueId", out var issueIdObj)
            && issueIdObj is string followUpSource
            && !string.IsNullOrWhiteSpace(followUpSource))
        {
            // The follow-up belongs to the RUN's project store, not
            // the runner's construction-time (primary) store.
            var followUpStore = (_issueStoreLookup is not null
                    && context.TryGetValue("projectId", out var fpidObj)
                    && fpidObj is string fpid
                    ? _issueStoreLookup(fpid)
                    : null) ?? _issues;
            // Operator model 2026-07-31: deferred findings are tracked
            // as drafts on the active sprint (materialized at
            // completion); only blocksIssueId filings become real
            // tasks immediately.
            var drafts = new Core.FollowUpDraftStore((Core.IssueStore)followUpStore);
            var followUpSprints = new Core.SprintStore((Core.IssueStore)followUpStore);
            tools.Add(new FollowUpTool(followUpStore, followUpSource, role.ToString(),
                drafts,
                activeSprintId: async ct2 => (await followUpSprints.GetActiveAsync(ct2))?.Id,
                sprints: followUpSprints).AsAIFunction());
        }

        // Run identity + start timestamp must exist BEFORE the chat
        // client is built: the activity tracker below wraps the raw
        // provider client and heartbeats per round-trip against this
        // run id. (The 'running' row itself is still written after
        // agent construction — a client-construction failure then
        // leaves no stale row.)
        var runId = Guid.NewGuid().ToString("N")[..12];
        var runTaskId = ResolveContextString(context, "issueId");
        var startedAt = DateTime.UtcNow;

        // Pause/resume phase tracking: the activity tracker reads
        // this closure per model round-trip so the dashboard shows
        // what the run is doing right now (a 3-minute verify round
        // no longer reads as stalled). verifyRound is bumped by the
        // in-session verification loop below.
        var verifyRound = 0;
        string? ComputePhase()
        {
            if (role == AgentType.Reviewer) return "reviewing";
            if (gateState is not null)
            {
                if (!gateState.PlanApproved) return "plan gate";
                if (verifyRound > 0) return $"verifying {verifyRound}/3";
                return "implementing";
            }
            return null;
        }

        var chatClient = _chatClientFactory.Create(_config, role, projectId);
        // Per-round-trip activity heartbeat. Wraps the RAW provider
        // client so MAF's internal model→tool→model loop (inside one
        // agent.RunAsync) is visible in near-real-time; wrapping the
        // outer client would only fire once per RunAsync call. Also
        // the token accounting point: provider usage is captured per
        // round-trip here and persisted to the run row (v31).
        var tracker = runStore is null
            ? null
            : new ActivityTrackingChatClient(chatClient, runId, runStore, ComputePhase);
        var trackedClient = (IChatClient?)tracker ?? chatClient;
        // Wrap with function invocation so MAF actually executes the
        // tools the model calls (instead of just leaving them in the
        // response). Cap raised from the 40 default: complex tasks
        // legitimately spend 40+ calls exploring before the first
        // edit — at 40 every run "completed" with 0 edits and the
        // no-diff path marked the task done (observed live: all six
        // tasks of the dispatcher-resilience sprint hollow-completed).
        var chatClientWithTools = tools.Count > 0
            ? BuildToolLoopClient(trackedClient, role, projectId)
            : trackedClient;

        var agent = new ChatClientAgent(
            chatClientWithTools,
            instructions: instructions,
            name: roleDef.AgentName,
            description: roleDef.ProjectSubdir,
            tools: tools);

        var message = new ChatMessage(ChatRole.User, fullPrompt);
        // Pause/resume: an explicit sessionId wins; otherwise the
        // runner resumes the persisted session for this
        // (project, task, role) when one exists — rework rounds AND
        // plain retries come back warm instead of cold. Junk in the
        // store degrades to a fresh session (logged).
        var sessionKey = SessionKey(projectId, runTaskId, role);
        var resumedSession = false;
        var session = await DeserializeSessionAsync(agent, sessionId, ct);
        if (session is null && sessionId is null && sessionKey is not null)
        {
            var storedJson = await RecallSessionAsync(sessionKey, ct);
            if (storedJson is not null)
            {
                session = await DeserializeSessionAsync(agent, storedJson, ct);
                resumedSession = session is not null;
            }
        }
        // Always run with a session so leaked-markup continuations below
        // keep the full conversation history.
        session ??= await agent.CreateSessionAsync(ct);

        // Run registry: visible in near real time as 'running'
        // (who is doing what); progress heartbeats land after every
        // model response (per-round-trip via the activity tracker,
        // plus a coarser stitch here after each continuation) and the
        // full transcript is persisted when the run finishes (partial
        // transcript on failure). Best-effort — never breaks a run.
        if (runStore is not null)
        {
            try
            {
                var (_, runModel, _) = _config.ResolveEffective(role, _modelOverrides, projectId);
                await runStore.StartAsync(runId, runTaskId, role.ToString(), runModel, ct,
                    resumedSession: resumedSession, projectId: projectId,
                    dispatchId: ResolveContextString(context, "dispatchId"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "agent_run start record failed for {Role}; continuing", role);
            }
        }

        // Transcript accumulation: AgentResponse.Messages holds only
        // the messages added by THAT call, so stitch the initial
        // prompt + each continuation turn together. Declared outside
        // the try so a mid-run failure still persists the partial
        // transcript — an agent that dies on turn 8 of 10 leaves 8
        // turns of auditable work, not nothing.
        var transcriptMessages = new List<ChatMessage> { message };
        // Set when the session is persisted on the success path; the
        // finally below persists the PARTIAL session on failure too —
        // a run that dies on turn 8 of 10 still leaves a resumable
        // conversation for the retry.
        var sessionPersisted = false;
        // Poisoned-session recovery: a run that dies mid-tool-loop
        // persists a session whose tail is an assistant tool_calls
        // message with no tool responses; every resume then 400s
        // deterministically ("must be followed by tool messages") —
        // observed live 2026-07-30: porthorizon task-20 burned two
        // dispatch cycles on the same poisoned session. Detect the
        // provider error, drop the stored session, and restart COLD
        // once. The flag also suppresses the finally's persist, which
        // would otherwise re-poison the key we just deleted.
        var sessionPoisoned = false;
        // Set by the poison catch so the finally skips disposing the
        // chat client — the cold restart reuses it. Cleared on loop
        // re-entry so the retry's own finally disposes normally.
        var restartPending = false;

        for (var poisonRestart = 0; ; poisonRestart++)
        {
        restartPending = false;
        try
        {
            var response = await agent.RunAsync(message, session, cancellationToken: ct);
            transcriptMessages.AddRange(response.Messages);
            await HeartbeatAsync(runStore, runId, transcriptMessages);

            // minimax-m3 quirk: near the end of long tool-call runs the
            // model sometimes emits its next tool call as literal text
            // markup ("]<]minimax[>[<tool_call>...<invoke name=...") in
            // the assistant content instead of a structured tool_calls
            // entry. MAF sees no tool calls and ends the loop
            // prematurely — the run "completes" with prose (+markup) as
            // the final answer and zero edits made. Detect the leak and
            // nudge the model to re-issue properly; bounded so a
            // persistently-degrading model cannot loop forever.
            const int maxContinuations = 3;
            for (var continuation = 0;
                 continuation < maxContinuations && HasLeakedToolCallMarkup(LastAssistantText(response));
                 continuation++)
            {
                _logger.LogWarning(
                    "Role {Role}: tool-call markup leaked into response text; nudging model to continue ({N}/{Max})",
                    role, continuation + 1, maxContinuations);
                var nudge = new ChatMessage(ChatRole.User, LeakedToolCallContinuationPrompt);
                response = await agent.RunAsync(
                    nudge,
                    session, cancellationToken: ct);
                transcriptMessages.Add(nudge);
                transcriptMessages.AddRange(response.Messages);
                await HeartbeatAsync(runStore, runId, transcriptMessages);
            }
            var elapsed = DateTime.UtcNow - startedAt;

            // Plan gate: a final rejection fails the run even though
            // the model conversation completed — the throw lands in
            // the catch below, which records 'failed' with the
            // partial transcript. The orchestrator's normal retry
            // machinery takes it from there.
            if (gateState?.PlanFailed == true)
            {
                await PersistGateRecordAsync(gateState, ResolveContextString(context, "issueId"), context, ct);
                throw new InvalidOperationException(
                    $"Plan rejected after {Gates.RunGateState.MaxRevisions} revisions: " +
                    (gateState.Verdicts.LastOrDefault().Feedback ?? "no feedback"));
            }
            await PersistGateRecordAsync(gateState, ResolveContextString(context, "issueId"), context, ct);

            // In-session pre-push verification: engineering runs verify
            // their work BEFORE completing — failures feed back to the
            // agent like a plan-gate revision (same session, lenient
            // budget), not a dispatch-level requeue with a fresh
            // worktree sync + plan gate. The CommitPushPr gate remains
            // as the backstop for whatever survives.
            var worktreePath = ResolveWorktreePath(context);
            var verifyCommands = ResolveVerifyCommands(projectId, worktreePath);
            if (gateState?.PlanApproved == true
                && worktreePath is not null
                && verifyCommands is { Count: > 0 })
            {
                const int maxVerifyRounds = 3;
                for (var round = 1; round <= maxVerifyRounds; round++)
                {
                    verifyRound = round;
                    var verify = VerifyRunner is not null
                        ? await VerifyRunner(worktreePath, verifyCommands, _logger, ct)
                        : await AgentTools.RunVerification.RunAsync(worktreePath, verifyCommands, _logger, ct);
                    if (verify.Ok)
                    {
                        _logger.LogInformation("Role {Role}: in-session verification passed (round {Round})", role, round);
                        break;
                    }
                    if (round == maxVerifyRounds)
                    {
                        // Out of rounds: return normally — the
                        // CommitPushPr gate re-verifies and bounces the
                        // task with the output (strike-counted).
                        _logger.LogWarning("Role {Role}: in-session verification still failing after {Rounds} rounds — the executor gate will handle it", role, maxVerifyRounds);
                        break;
                    }
                    _logger.LogInformation("Role {Role}: verification failed; feeding output back into the session (round {Round}/{Max})",
                        role, round, maxVerifyRounds);
                    var feedback = new ChatMessage(ChatRole.User,
                        "Pre-push verification failed (round " + round + "/" + maxVerifyRounds + "). " +
                        "Fix the failures below and iterate — verification runs again when you finish. " +
                        "Do not weaken tests to make them pass; fix the cause.\n\n" +
                        string.Join("\n\n", verify.Failures));
                    response = await agent.RunAsync(feedback, session, cancellationToken: ct);
                    transcriptMessages.Add(feedback);
                    transcriptMessages.AddRange(response.Messages);
                    await HeartbeatAsync(runStore, runId, transcriptMessages);
                }
            }

            var text = string.Concat(response.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .Select(m => m.Text));
            // Pause/resume: persist the session so the next run of
            // this (project, task, role) resumes warm. The returned
            // reference is the storage KEY, not the blob — issue
            // metadata and logs must not carry the full transcript.
            var newSessionId = await SerializeAndPersistSessionAsync(agent, session, sessionKey, ct);
            sessionPersisted = newSessionId is not null;

            // DIAGNOSTIC: append to a side-channel log so we can
            // diagnose the silent-agent bug even when the host swallows
            // stdout. Path is set by Program.cs (state root); skipped
            // when unset. Best-effort: never breaks a run.
            try
            {
                var diagLog = DiagnosticLogPath;
                if (!string.IsNullOrEmpty(diagLog))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(diagLog)!);
                    using var sw = new StreamWriter(diagLog, append: true);
                    sw.WriteLine($"--- {DateTime.Now:O} role={role} msgs={response.Messages.Count} text_len={text.Length} tool_msgs={response.Messages.Count(m => m.Role == ChatRole.Tool)} session_id={newSessionId ?? "<null>"} ---");
                    foreach (var m in response.Messages)
                    {
                        var preview = (m.Text ?? "");
                        if (preview.Length > 400) preview = preview.Substring(0, 400) + "...";
                        var toolCalls = m.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>()
                            .Select(c => $"{c.Name}({string.Join(",", c.Arguments?.Keys ?? new System.Collections.Generic.List<string>())})")
                            .ToList();
                        sw.WriteLine($"  msg role={m.Role} text_len={(m.Text ?? "").Length} tool_calls=[{string.Join(";", toolCalls)}] preview={preview}");
                    }
                    sw.Flush();
                }
            }
            catch
            {
                // best-effort
            }

            if (runStore is not null)
            {
                try
                {
                    var transcript = BuildTranscriptJson(transcriptMessages);
                    var toolCalls = transcriptMessages.Sum(m =>
                        m.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>().Count());
                    await runStore.FinishAsync(runId, "succeeded",
                        (long)elapsed.TotalMilliseconds,
                        transcriptMessages.Count, toolCalls, text.Length,
                        error: null, transcriptJson: transcript, ct: CancellationToken.None,
                        inputTokens: tracker?.TotalInputTokens,
                        outputTokens: tracker?.TotalOutputTokens,
                        cacheReadTokens: tracker?.TotalCacheReadTokens);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "agent_run finish record failed for {Role}; continuing", role);
                }
            }

            return new AgentRunResult(
    Text: text,
    SessionId: newSessionId,
    InputTokens: tracker?.TotalInputTokens ?? 0,
    OutputTokens: tracker?.TotalOutputTokens ?? 0,
    Elapsed: elapsed);
        }
        catch (Exception ex) when (poisonRestart == 0 && IsPoisonedSessionError(ex))
        {
            _logger.LogWarning(
                "Role {Role}: persisted session is unusable (provider rejected it: dangling tool_calls or transcript over the request cap); dropping it and restarting cold",
                role);
            if (sessionKey is not null && _memory is not null)
            {
                try { await _memory.ForgetAsync(sessionKey, CancellationToken.None); }
                catch (Exception forgetEx) { _logger.LogDebug(forgetEx, "poisoned session delete failed for {Key}; continuing", sessionKey); }
            }
            sessionPoisoned = true;
            restartPending = true;
            session = await agent.CreateSessionAsync(ct);
            transcriptMessages = new List<ChatMessage> { message };
            // Loop iterates: one cold restart with the same prompt.
        }
        catch (Exception ex) when (runStore is not null)
        {
            try
            {
                // Partial transcript: whatever turns completed before
                // the failure are still persisted — the operator can
                // see exactly how far the agent got.
                var partial = transcriptMessages.Count > 1
                    ? BuildTranscriptJson(transcriptMessages)
                    : null;
                var toolCalls = transcriptMessages.Sum(m =>
                    m.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>().Count());
                var textChars = transcriptMessages
                    .Where(m => m.Role == ChatRole.Assistant)
                    .Sum(m => (m.Text ?? "").Length);
                await runStore.FinishAsync(runId, "failed",
                    (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                    transcriptMessages.Count, toolCalls, textChars,
                    $"{ex.GetType().Name}: {ex.Message}",
                    transcriptJson: partial, ct: CancellationToken.None,
                    inputTokens: tracker?.TotalInputTokens,
                    outputTokens: tracker?.TotalOutputTokens,
                    cacheReadTokens: tracker?.TotalCacheReadTokens);
            }
            catch { /* best-effort */ }
            throw;
        }
        finally
        {
            // Pause/resume: persist the session on failure too
            // (partial sessions resume fine — the retry continues
            // where this run died instead of re-exploring from
            // scratch). Best-effort; never masks the real exception.
            // Skipped on a poison restart: the old session is exactly
            // what we just deleted from the store — persisting it
            // would re-poison the key before the cold retry reads it.
            if (!sessionPersisted && !sessionPoisoned)
            {
                try
                {
                    await SerializeAndPersistSessionAsync(agent, session, sessionKey, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "session persist on failure path failed; continuing");
                }
            }
            // MAF ChatClientAgent does not implement IDisposable; chatClient is the
            // resource, and our stubbed IChatClient (Microsoft.Extensions.AI) is
            // IDisposable. Best-effort dispose; real providers handle their own
            // connection pools. When function invocation is in play, the
            // ChatClientBuilder wrapper is what holds the underlying client.
            // Skipped while a poison restart is pending — the cold
            // retry still needs the client.
            if (!restartPending)
            {
                var disposable = chatClientWithTools as IDisposable ?? chatClient;
                if (disposable is IDisposable d) d.Dispose();
            }
        }
        }
    }

    /// <summary>True for the provider 400 that a poisoned session
    /// produces: an assistant message with tool_calls not followed by
    /// the matching tool responses. Walks the inner-exception chain —
    /// the gateway wraps the upstream OpenAI error.</summary>
    /// <summary>
    /// Session-is-the-problem errors: the provider rejects the
    /// RESUMED transcript, not the request shape. Two classes:
    /// (a) structural poison — dangling tool_calls after a mid-run
    /// kill; (b) size — the accumulated transcript (rework rounds,
    /// full-file reads) blows the provider's request cap (observed
    /// live 2026-08-01: task-365, "total message size 35670664
    /// exceeds limit" / "exceeded model token limit" — every retry
    /// 400s instantly until the session is dropped). Both recover
    /// the same way: forget the session, cold-restart once — the
    /// prompt's rework context carries what matters.
    /// </summary>
    internal static bool IsPoisonedSessionError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e.Message.Contains("tool_calls", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("must be followed by tool messages", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (e.Message.Contains("exceeded model token limit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (e.Message.Contains("total message size", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // MiniMax phrasing (observed live 2026-08-06: task-546,
            // "invalid params, context window exceeds limit (2013)"
            // — an intermittent kilo-gateway upstream 400; the
            // cold-restart retry lands on a healthy upstream).
            if (e.Message.Contains("context window exceeds limit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // MiniMax Anthropic-endpoint phrasing (observed live
            // 2026-08-14: porthorizon task-525, "invalid params, tool
            // call result does not follow tool call (2013)" — a
            // persisted session with a dangling tool_use/tool_result
            // pair 400'd every resume; requeue alone could never
            // recover because the poisoned blob was replayed each
            // time). Same failure family as the OpenAI phrasing above.
            if (e.Message.Contains("tool call result does not follow tool call", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // Anthropic-native phrasings for the same pairing break:
            // tool_use ids without tool_result blocks, or a tool_result
            // with an unknown tool_use_id.
            if (e.Message.Contains("tool_use", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("tool_result", StringComparison.OrdinalIgnoreCase)
                && (e.Message.Contains("without", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains("unexpected", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ResolveWorktreePath(IReadOnlyDictionary<string, object>? context)
    {
        if (context is null) return null;
        if (!context.TryGetValue("worktreePath", out var raw) || raw is null) return null;
        return raw.ToString();
    }

    /// <summary>
    /// The tool-loop pipeline: function invocation outermost, the
    /// context-window reducer INSIDE it so every model→tool→model
    /// round-trip is budget-checked, not just the first. Without
    /// compaction a long engineering run accumulates unbounded tool
    /// results (file reads, bash output) until the provider 400s
    /// mid-run — observed live 2026-08-06: task-560, 382 messages /
    /// 481KB transcript, killed when minimax-m3's window overflowed
    /// (operator-approved fix). The audit transcript
    /// (transcriptMessages) keeps the FULL text; only provider-bound
    /// requests are compacted. Disabled per provider when
    /// ContextWindowTokens is unset (unknown window — never guess).
    /// </summary>
    private IChatClient BuildToolLoopClient(IChatClient trackedClient, AgentType role, string? projectId)
    {
        var builder = new ChatClientBuilder(trackedClient)
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 200);
        var (windowTokens, maxOutputTokens) = ResolveCompactionBudget(role, projectId);
        if (windowTokens is not null)
        {
            // MAAI001: MAF's compaction strategies are marked
            // evaluation-only in 1.12. Accepted risk — the reducer
            // only rewrites provider-BOUND requests; the audit
            // transcript and session state are untouched, so a
            // future API break is a build error here, not a runtime
            // behavior change.
#pragma warning disable MAAI001
            builder.UseChatReducer(
                new Microsoft.Agents.AI.Compaction.ContextWindowCompactionStrategy(
                    windowTokens.Value, maxOutputTokens).AsChatReducer(),
                configure: null);
#pragma warning restore MAAI001
        }
        return builder.Build();
    }

    private (int? WindowTokens, int MaxOutputTokens) ResolveCompactionBudget(AgentType role, string? projectId)
    {
        try
        {
            var (provider, _, _) = _config.ResolveEffective(role, _modelOverrides, projectId);
            return (provider.ContextWindowTokens, provider.MaxOutputTokens ?? 8192);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "compaction budget resolution failed for {Role}; running without compaction", role);
            return (null, 8192);
        }
    }

    /// <summary>Verify commands for a run: the project's $verify
    /// override; null means auto-detect against the worktree (dotnet
    /// build+test for dotnet repos); empty means disabled.</summary>
    private IReadOnlyList<string>? ResolveVerifyCommands(string? projectId, string? worktreePath)
    {
        var configured = projectId is not null && _verifyCommandsLookup is not null
            ? _verifyCommandsLookup(projectId)
            : null;
        if (configured is not null) return configured;
        return worktreePath is not null
            ? AgentTools.RunVerification.DefaultCommands(worktreePath)
            : null;
    }

    /// <summary>Gate factory for the run-gate pipeline. Unknown names
    /// return null (the pipeline skips them with a warning).</summary>
    private Gates.IRunGate? BuildRunGate(string name, string? projectId) => name switch
    {
        Gates.PlanSchemaGate.GateName => new Gates.PlanSchemaGate(),
        Gates.PlanTerritoryGate.GateName => new Gates.PlanTerritoryGate(),
        Gates.PlanLlmReviewGate.GateName => new Gates.PlanLlmReviewGate(
            () => _chatClientFactory.Create(_config, AgentType.Reviewer, projectId), _logger),
        _ => null,
    };

    /// <summary>Persist the plan-gate audit trail to the task's
    /// metadata (best-effort, never breaks a run). The TaskDetail
    /// page renders it.</summary>
    private async Task PersistGateRecordAsync(
        Gates.RunGateState? gateState, string? issueId,
        IReadOnlyDictionary<string, object>? context, CancellationToken ct)
    {
        if (gateState is null || issueId is null || _issues is null) return;
        if (gateState.Verdicts.Count == 0) return;
        try
        {
            // The audit row is workload data for the OWNING project's
            // store (same rule as the follow-up tool, 2026-07-31).
            // Using the construction-time primary store silently
            // dropped every non-primary project's plan-gate audit
            // (observed 2026-08-11: task-12/task-652 rejections left
            // no planGate metadata — the rejected plan was invisible
            // to the operator).
            var store = (_issueStoreLookup is not null
                    && context is not null
                    && context.TryGetValue("projectId", out var pidObj)
                    && pidObj is string pid
                    ? _issueStoreLookup(pid)
                    : null) ?? _issues;
            var task = await store.GetAsync(issueId, ct);
            if (task is null) return;
            var record = new
            {
                approved = gateState.PlanApproved,
                fastPath = gateState.FastPath,
                revisions = gateState.Revisions,
                failed = gateState.PlanFailed,
                plan = gateState.PlanText is { Length: > 3000 } p ? p[..3000] : gateState.PlanText,
                verdicts = gateState.Verdicts.Select(v => new { gate = v.Gate, outcome = v.Outcome.ToString(), feedback = v.Feedback.Length > 500 ? v.Feedback[..500] : v.Feedback }).ToList(),
            };
            await store.TransitionAsync(issueId, task.Status, error: null,
                metadata: new Dictionary<string, object>
                {
                    ["planGate"] = System.Text.Json.JsonSerializer.Serialize(record),
                }, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "plan-gate record persist failed for {IssueId}; continuing", issueId);
        }
    }

    private static string? ResolveContextString(IReadOnlyDictionary<string, object>? context, string key)
    {
        if (context is null) return null;
        return context.TryGetValue(key, out var raw) ? raw?.ToString() : null;
    }

    /// <summary>
    /// Serialize a run's full conversation for the run-detail view:
    /// roles, text, tool calls (name + args), and tool results.
    /// Full-fidelity with per-field caps (50KB text, 20KB results)
    /// so a pathological single message can't blow up a row; table
    /// size is bounded by AgentRunStore retention, not truncation.
    /// </summary>
    /// <summary>
    /// Mid-run progress heartbeat for the run registry: updates turn /
    /// tool-call / text counts + last_activity_at so the dashboard can
    /// tell "actively working" from "hung waiting on the provider".
    /// Best-effort — never breaks a run.
    /// </summary>
    private async Task HeartbeatAsync(AgentRunStore? runStore, string runId, List<ChatMessage> transcriptMessages)
    {
        if (runStore is null) return;
        try
        {
            var toolCalls = transcriptMessages.Sum(m =>
                m.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>().Count());
            var textChars = transcriptMessages
                .Where(m => m.Role == ChatRole.Assistant)
                .Sum(m => (m.Text ?? "").Length);
            await runStore.UpdateProgressAsync(runId, transcriptMessages.Count, toolCalls, textChars,
                transcriptJson: BuildTranscriptJson(transcriptMessages),
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "agent_run progress heartbeat failed for run {RunId}; continuing", runId);
        }
    }

    internal static string BuildTranscriptJson(IEnumerable<ChatMessage> messages)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var m in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", m.Role.Value);
                writer.WritePropertyName("contents");
                writer.WriteStartArray();
                foreach (var c in m.Contents)
                {
                    switch (c)
                    {
                        case Microsoft.Extensions.AI.TextReasoningContent thinking:
                            writer.WriteStartObject();
                            writer.WriteString("type", "thinking");
                            writer.WriteString("text", Cap(thinking.Text, 50_000));
                            writer.WriteEndObject();
                            break;
                        case Microsoft.Extensions.AI.TextContent text:
                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", Cap(text.Text, 50_000));
                            writer.WriteEndObject();
                            break;
                        case Microsoft.Extensions.AI.FunctionCallContent call:
                            writer.WriteStartObject();
                            writer.WriteString("type", "tool_call");
                            writer.WriteString("name", call.Name);
                            writer.WriteString("callId", call.CallId);
                            writer.WriteString("args", Cap(JsonSerializer.Serialize(call.Arguments), 20_000));
                            writer.WriteEndObject();
                            break;
                        case Microsoft.Extensions.AI.FunctionResultContent result:
                            writer.WriteStartObject();
                            writer.WriteString("type", "tool_result");
                            writer.WriteString("callId", result.CallId);
                            writer.WriteString("result", Cap(
                                result.Result?.ToString() ?? "", 20_000));
                            writer.WriteEndObject();
                            break;
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Cap(string? s, int n)
        => s is null ? "" : s.Length <= n ? s : s[..n] + "…[truncated]";

    /// <summary>
    /// Build the secrets-by-reference environment for the agent's bash
    /// tool. Every stored kind for the project becomes
    /// <c>FORGE_SECRET_&lt;KIND&gt;</c> (uppercased, '-' → '_');
    /// <c>github_token</c> also maps to the conventional
    /// <c>GITHUB_TOKEN</c>. Values are decrypted here and injected into
    /// the spawned process environment — they never appear in the
    /// model's prompt, tool-call JSON, or logs. Returns null when no
    /// project context or no secrets are stored.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> ResolveSecretEnvAsync(
        IReadOnlyDictionary<string, object>? context, CancellationToken ct)
    {
        if (_secrets is null || context is null) return null;
        if (!context.TryGetValue("projectId", out var raw) || raw is null) return null;
        var projectId = raw.ToString();
        if (string.IsNullOrWhiteSpace(projectId)) return null;

        IReadOnlyList<SecretRecord> stored;
        try
        {
            stored = await _secrets.ListAsync(projectId, ct);
        }
        catch (Exception ex)
        {
            // Secret lookup must never break a dispatch; the agent
            // just runs without the env vars.
            _logger.LogWarning(ex, "Failed to list secrets for project {ProjectId}; continuing without secret env", projectId);
            return null;
        }
        if (stored.Count == 0) return null;

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var meta in stored)
        {
            string? plaintext;
            try
            {
                plaintext = await _secrets.GetPlaintextAsync(projectId, meta.Kind, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt secret {Kind} for project {ProjectId}; skipping", meta.Kind, projectId);
                continue;
            }
            if (string.IsNullOrEmpty(plaintext)) continue;

            env[$"FORGE_SECRET_{meta.Kind.Replace('-', '_').ToUpperInvariant()}"] = plaintext;
            if (string.Equals(meta.Kind, SecretKinds.GitHubToken, StringComparison.OrdinalIgnoreCase))
            {
                env["GITHUB_TOKEN"] = plaintext;
            }
        }
        return env.Count == 0 ? null : env;
    }

    private async Task<string> BuildSkillInstructionsAsync(AgentType role, string? projectId, CancellationToken ct)
    {
        IReadOnlyList<SkillContent> skills;
        try
        {
            skills = await _skills!.LoadForRoleAsync(role, projectId, ct);
        }
        catch (Exception ex)
        {
            // Skill loading must never break a dispatch. The role prompt
            // (without skills) still reaches the agent, and the error is
            // surfaced via the dashboard event log.
            _logger.LogWarning(ex, "Failed to load skills for role {Role} project {ProjectId}; continuing without skills", role, projectId ?? "<none>");
            return string.Empty;
        }
        if (skills.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Project skills");
        sb.AppendLine();
        sb.AppendLine(
            "The following skills are available in this project. Apply them where relevant; " +
            "do not quote them verbatim unless the task asks for it.");
        sb.AppendLine();
        foreach (var s in skills)
        {
            sb.Append("### ").Append(s.Name).AppendLine();
            if (!string.IsNullOrEmpty(s.Description))
            {
                sb.AppendLine(s.Description);
            }
            sb.AppendLine();
            sb.AppendLine(s.Body);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Sprint flow: when the dispatch context carries sprint fields
    /// (RunAgentExecutor sets them for issues in the ACTIVE sprint),
    /// render the shared sprint context — goal + sibling roster — so
    /// every agent in the sprint works toward the same goal with
    /// visibility of each other's tasks.
    /// </summary>
    private static string BuildSprintBlock(IReadOnlyDictionary<string, object>? context)
    {
        if (context is null) return string.Empty;
        if (!context.TryGetValue("sprintId", out var rawId) || rawId is null) return string.Empty;
        var sprintId = rawId.ToString();
        if (string.IsNullOrWhiteSpace(sprintId)) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Sprint");
        sb.AppendLine();
        if (context.TryGetValue("sprintName", out var rawName) && rawName?.ToString() is { Length: > 0 } name)
        {
            sb.Append("You are working in sprint **").Append(name).Append("**");
        }
        else
        {
            sb.Append("You are working in sprint `").Append(sprintId).Append('`');
        }
        if (context.TryGetValue("sprintGoal", out var rawGoal) && rawGoal?.ToString() is { Length: > 0 } goal)
        {
            sb.Append(". Goal: ").AppendLine(goal);
        }
        else
        {
            sb.AppendLine(".");
        }
        if (context.TryGetValue("sprintRoster", out var rawRoster) && rawRoster?.ToString() is { Length: > 0 } roster)
        {
            sb.AppendLine();
            sb.AppendLine("Sibling tasks in this sprint (coordinate; don't duplicate their work):");
            sb.AppendLine(roster);
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> BuildMemoryInstructionsAsync(
        IReadOnlyDictionary<string, object>? context, CancellationToken ct)
    {
        var sections = new List<string>();
        // Sprint-scoped memory first: memories persisted by sibling
        // tasks under `sprint/{id}/` (MemoryExtractor dual-persists
        // when the issue is in the ACTIVE sprint).
        if (context is not null
            && context.TryGetValue("sprintId", out var rawSprint) && rawSprint is not null
            && rawSprint.ToString() is { Length: > 0 } sprintId)
        {
            try
            {
                var sprintMemories = await _memory!.RecallAsync($"sprint/{sprintId}/", ct);
                var rendered = MemoryStore.RenderSectionForPrompt("## Sprint memory", sprintMemories);
                if (!string.IsNullOrEmpty(rendered)) sections.Add(rendered);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to recall sprint memory; continuing without it");
            }
        }
        IReadOnlyList<MemoryRecord> memories;
        try
        {
            // Exclusions applied in SQL: session/ blobs are hundreds
            // of KB each (71.6MB across 284 rows on 2026-08-08) and
            // are filtered out below anyway — reading them per run
            // strangled the Basic-tier DB under concurrency.
            memories = await _memory!.RecallAsync(keyPrefix: null,
                excludePrefixes: new[] { "sprint/", "session/" }, ct);
        }
        catch (Exception ex)
        {
            // Memory recall must never break a dispatch. Errors are
            // logged and the agent runs without the memory block.
            _logger.LogWarning(ex, "Failed to recall project memory; continuing without it");
            return string.Join("\n\n", sections);
        }
        // Sprint keys already have their own section above; session
        // keys are machine state (persisted MAF sessions for
        // pause/resume), not prompt material. Keep the global block
        // free of both.
        var globalOnly = memories
            .Where(m => !m.Key.StartsWith("sprint/", StringComparison.Ordinal)
                && !m.Key.StartsWith("session/", StringComparison.Ordinal))
            .ToList();
        var globalRendered = MemoryStore.RenderForPrompt(globalOnly);
        if (!string.IsNullOrEmpty(globalRendered)) sections.Add(globalRendered);
        return string.Join("\n\n", sections);
    }

    /// <summary>Per-project role-prompt root: the project's own
    /// <c>&lt;root&gt;/agents</c> dir when it ships one, else the
    /// construction-time fallback (built-in defaults). Resolved once
    /// per project and cached; a project that gains an agents/ dir
    /// picks it up on restart.</summary>
    private string ResolvePromptRoot(string? projectId)
    {
        if (projectId is null || _projectRootLookup is null) return _rolePromptsRoot;
        return _promptRootsByProject.GetOrAdd(projectId, ResolvePromptRootUncached);
    }

    // Internal for tests: the per-project resolution without the cache.
    internal string ResolvePromptRootUncached(string projectId)
    {
        string? root = null;
        try { root = _projectRootLookup!(projectId); }
        catch (Exception ex) { _logger.LogWarning(ex, "project root lookup failed for {ProjectId}; using fallback prompt root", projectId); }
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(Path.Combine(root, "agents")))
            return Path.Combine(root, "agents");
        return _rolePromptsRoot;
    }

    private string LoadRoleInstructions(string agentName, string? projectId = null)
    {
        var path = Path.Combine(ResolvePromptRoot(projectId), agentName + ".md");
        if (!File.Exists(path))
        {
            _logger.LogWarning("role prompt file not found at {Path}; using fallback instructions", path);
            return $"You are the {agentName} agent.";
        }
        // Minimal YAML frontmatter parser: the file is `--- description: ...\n rest`. We
        // return the description field as the MAF instructions. Multi-line YAML,
        // nested keys, and edge cases are out of scope for Phase 0; we refine in
        // P1.5 (or use a real YAML lib) when the agent prompt matures.
        var text = File.ReadAllText(path);
        var inFence = false;
        var desc = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("---"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence && line.StartsWith("description:"))
            {
                desc.AppendLine(line["description:".Length..].Trim());
            }
        }
        if (desc.Length == 0) desc.AppendLine($"You are the {agentName} agent.");
        return desc.ToString().Trim();
    }

    private async Task<AgentSession?> DeserializeSessionAsync(
        ChatClientAgent agent, string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(sessionId);
            return await agent.DeserializeSessionAsync(json, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize session; starting fresh");
            return null;
        }
    }

    /// <summary>
    /// Nudge sent when the model emits a tool call as plain-text markup
    /// (see the minimax-m3 note in RunAsync). Deliberately short: the
    /// model has the full conversation in its session already.
    /// </summary>
    private const string LeakedToolCallContinuationPrompt =
        "Your previous message contained a tool call emitted as plain-text markup, which cannot be executed. " +
        "If you intended to call a tool, re-issue it now as a proper tool call. " +
        "If you have already completed the task, reply with a brief summary of what you changed (no markup).";

    private static string LastAssistantText(AgentResponse response) =>
        response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? string.Empty;

    /// <summary>
    /// True when assistant text contains tool-call markup that leaked
    /// into the content channel instead of arriving as structured
    /// tool_calls. Internal for tests.
    /// </summary>
    internal static bool HasLeakedToolCallMarkup(string text) =>
        text.Contains("]<]minimax[>", StringComparison.Ordinal) ||
        text.Contains("<tool_call>", StringComparison.Ordinal) ||
        text.Contains("<invoke name=", StringComparison.Ordinal);

    /// <summary>
    /// The memory-store key a run's persisted MAF session lives under:
    /// <c>session/&lt;projectId|_&gt;/&lt;taskId&gt;/&lt;role&gt;</c>.
    /// Null when there's no task id — untasked runs (groomer,
    /// designer, intake) don't persist sessions.
    /// </summary>
    internal static string? SessionKey(string? projectId, string? taskId, AgentType role)
        => string.IsNullOrWhiteSpace(taskId)
            ? null
            : $"session/{(string.IsNullOrWhiteSpace(projectId) ? "_" : projectId)}/{taskId}/{role}";

    /// <summary>
    /// Serialize the run's MAF session and persist it under the
    /// (project, task, role) session key so the NEXT run of the same
    /// task+role resumes warm (the pause/resume rework loop). Returns
    /// the storage key as the run's session reference — never the
    /// blob: issue metadata and logs must not carry 100KB of
    /// transcript. Best-effort: failures degrade to a null
    /// reference, never throw.
    /// </summary>
    private async Task<string?> SerializeAndPersistSessionAsync(
        ChatClientAgent agent, AgentSession session, string? sessionKey, CancellationToken ct)
    {
        try
        {
            if (sessionKey is null || _memory is null) return null;
            var json = await agent.SerializeSessionAsync(session, cancellationToken: ct);
            // TTL: a session is useful only while its task is live
            // (rework rounds, reviewer re-review). Sessions for
            // terminal tasks are dead weight — 284 orphaned blobs
            // accumulated to 71.6MB by 2026-08-08 and nearly took
            // the service down. 14 days covers even the slowest
            // rework loop.
            await _memory.RememberAsync(sessionKey, json.GetRawText(), ttlDays: 14, CancellationToken.None);
            return sessionKey;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session persist failed for {Key}; continuing", sessionKey ?? "<none>");
            return null;
        }
    }

    /// <summary>
    /// Recall the persisted session JSON for a session key. Missing
    /// key / store failure → null (the caller starts fresh).
    /// </summary>
    private async Task<string?> RecallSessionAsync(string key, CancellationToken ct)
    {
        if (_memory is null) return null;
        try
        {
            var hits = await _memory.RecallAsync(key, ct);
            return hits.LastOrDefault()?.Body;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session recall failed for {Key}; starting fresh", key);
            return null;
        }
    }
}

