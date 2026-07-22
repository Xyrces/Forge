using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
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
    private readonly ISkillSource? _skills;
    private readonly MemoryStore? _memory;
    private readonly ContextHandoffStore? _handoffs;
    private readonly Func<DesignArtifactStore?>? _designArtifactsFactory;
    private readonly Func<ISpecStore?>? _specsFactory;
    private readonly Func<ArtOutputStore?>? _artOutputsFactory;
    private readonly ISecretStore? _secrets;

    public MafAgentRunner(
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        ILogger<MafAgentRunner> logger,
        ISkillSource? skills = null,
        string rolePromptsRoot = "agents",
        MemoryStore? memory = null,
        ContextHandoffStore? handoffs = null,
        // P5.1 stores — passed as factories so the runner can
        // be constructed before the stores are (avoids a Program.cs
        // re-ordering). The factories are invoked once on first
        // tool build; the result is cached.
        Func<DesignArtifactStore?>? designArtifacts = null,
        Func<ISpecStore?>? specs = null,
        Func<ArtOutputStore?>? artOutputs = null,
        ISecretStore? secrets = null)
    {
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _logger = logger;
        _skills = skills;
        _rolePromptsRoot = rolePromptsRoot;
        _memory = memory;
        _handoffs = handoffs;
        _designArtifactsFactory = designArtifacts;
        _specsFactory = specs;
        _artOutputsFactory = artOutputs;
        _secrets = secrets;
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
        var roleInstructions = LoadRoleInstructions(roleDef.AgentName);
        var skillInstructions = _skills is null
            ? string.Empty
            : await BuildSkillInstructionsAsync(role, ct);
        var memoryInstructions = _memory is null
            ? string.Empty
            : await BuildMemoryInstructionsAsync(ct);
        var instructions = string.Join("\n\n", new[]
        {
            roleInstructions,
            skillInstructions,
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
        if (!string.IsNullOrWhiteSpace(bashWorkingDir))
        {
            var secretEnv = await ResolveSecretEnvAsync(context, ct);
            tools.Add(new BashTool(bashWorkingDir, logger: null, envVars: secretEnv).AsAIFunction());
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

        var chatClient = _chatClientFactory.Create(_config, role);
        // Wrap with function invocation so MAF actually executes the
        // tools the model calls (instead of just leaving them in the
        // response).
        var chatClientWithTools = tools.Count > 0
            ? new ChatClientBuilder(chatClient).UseFunctionInvocation().Build()
            : chatClient;

        var agent = new ChatClientAgent(
            chatClientWithTools,
            instructions: instructions,
            name: roleDef.AgentName,
            description: roleDef.ProjectSubdir,
            tools: tools);

        var message = new ChatMessage(ChatRole.User, fullPrompt);
        var session = await DeserializeSessionAsync(agent, sessionId, ct);

        try
        {
            var startedAt = DateTime.UtcNow;
            var response = session is null
                ? await agent.RunAsync(message, cancellationToken: ct)
                : await agent.RunAsync(message, session, cancellationToken: ct);
            var elapsed = DateTime.UtcNow - startedAt;

            var text = string.Concat(response.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .Select(m => m.Text));
            var newSessionId = await SerializeSessionAsync(response, agent, session, ct);

            // DIAGNOSTIC: append to a side-channel log so we can
            // diagnose the silent-agent bug even when the SCM swallows
            // stdout. The file lives in C:\ProgramData\Forge\agent.log
            // and is appended on every agent run.
            try
            {
                var diagLog = @"C:\ProgramData\Forge\agent.log";
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
            catch
            {
                // best-effort
            }

            return new AgentRunResult(
    Text: text,
    SessionId: newSessionId,
    InputTokens: 0,
    OutputTokens: 0,
    Elapsed: elapsed);
        }
finally
        {
            // MAF ChatClientAgent does not implement IDisposable; chatClient is the
            // resource, and our stubbed IChatClient (Microsoft.Extensions.AI) is
            // IDisposable. Best-effort dispose; real providers handle their own
            // connection pools. When function invocation is in play, the
            // ChatClientBuilder wrapper is what holds the underlying client.
            var disposable = chatClientWithTools as IDisposable ?? chatClient;
            if (disposable is IDisposable d) d.Dispose();
        }
    }

    private static string? ResolveWorktreePath(IReadOnlyDictionary<string, object>? context)
    {
        if (context is null) return null;
        if (!context.TryGetValue("worktreePath", out var raw) || raw is null) return null;
        return raw.ToString();
    }

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

    private async Task<string> BuildSkillInstructionsAsync(AgentType role, CancellationToken ct)
    {
        IReadOnlyList<SkillContent> skills;
        try
        {
            skills = await _skills!.LoadForRoleAsync(role, ct);
        }
        catch (Exception ex)
        {
            // Skill loading must never break a dispatch. The role prompt
            // (without skills) still reaches the agent, and the error is
            // surfaced via the dashboard event log.
            _logger.LogWarning(ex, "Failed to load skills for role {Role}; continuing without skills", role);
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

    private async Task<string> BuildMemoryInstructionsAsync(CancellationToken ct)
    {
        IReadOnlyList<MemoryRecord> memories;
        try
        {
            memories = await _memory!.RecallAsync(keyPrefix: null, ct);
        }
        catch (Exception ex)
        {
            // Memory recall must never break a dispatch. Errors are
            // logged and the agent runs without the memory block.
            _logger.LogWarning(ex, "Failed to recall project memory; continuing without it");
            return string.Empty;
        }
        return MemoryStore.RenderForPrompt(memories);
    }

    private string LoadRoleInstructions(string agentName)
    {
        var path = Path.Combine(_rolePromptsRoot, agentName + ".md");
        if (!File.Exists(path))
        {
            _logger.LogWarning("role prompt file not found at {Path}; using fallback instructions", path);
            return $"You are the {agentName} agent for the PortHorizon project.";
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
        if (desc.Length == 0) desc.AppendLine($"You are the {agentName} agent for the PortHorizon project.");
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

    private async Task<string?> SerializeSessionAsync(
        AgentResponse response, ChatClientAgent agent, AgentSession? session, CancellationToken ct)
    {
        // SerializeSessionAsync is on the protected base; the public API is
        // SerializeSessionAsync(AgentSession, ...). We can't call the protected
        // override directly, so we use the agent's own public surface if
        // exposed. Phase 0 doesn't yet need round-tripping; return null and let
        // the dashboard show "(no session)".
        // TODO P1: expose this via a derived ChatClientAgent subclass or via
        // AgentSession.Serialize/Deserialize directly.
        await Task.CompletedTask;
        return null;
    }
}

