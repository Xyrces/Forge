using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Agents;

/// <summary>
/// MAF-based implementation of <see cref="IAgentRunner"/>. Phase 0:
/// wraps <see cref="ChatClientAgent"/>, instantiated fresh per call with
/// the role's instructions loaded from the .kilo/agents/<role>.md
/// frontmatter <c>description</c> field.
///
/// <para>
/// The runner does NOT itself manage worktrees, commits, pushes, or PRs.
/// Those are AIFunctions the agent invokes (P2). Phase 0 runs the
/// agent in plain text mode (no tools) and asserts the response shape.
/// </para>
/// </summary>
public sealed class MafAgentRunner : IAgentRunner
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly ILogger<MafAgentRunner> _logger;
    private readonly string _kiloAgentsRoot;

    public MafAgentRunner(
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        ILogger<MafAgentRunner> logger,
        string kiloAgentsRoot = ".kilo/agents")
    {
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _logger = logger;
        _kiloAgentsRoot = kiloAgentsRoot;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentType role, string prompt, string? sessionId, CancellationToken ct)
    {
        var roleDef = _roles.ForType(role);
        var instructions = LoadRoleInstructions(roleDef.KiloAgentName);
        var fullPrompt = instructions + "\n\n" + prompt;

        var chatClient = _chatClientFactory.Create(_config);
        var agent = new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: roleDef.KiloAgentName,
            description: roleDef.ProjectSubdir);

        var message = new ChatMessage(ChatRole.User, fullPrompt);
        var session = await DeserializeSessionAsync(agent, sessionId, ct);

        try
        {
            var response = session is null
                ? await agent.RunAsync(message, cancellationToken: ct)
                : await agent.RunAsync(message, session, cancellationToken: ct);

            var text = string.Concat(response.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .Select(m => m.Text));
            var newSessionId = await SerializeSessionAsync(response, agent, session, ct);

            return new AgentRunResult(
                Text: text,
                SessionId: newSessionId,
                InputTokens: 0,
                OutputTokens: 0);
        }
        finally
        {
            // MAF ChatClientAgent does not implement IDisposable; chatClient is the
            // resource, and our stubbed IChatClient (Microsoft.Extensions.AI) is
            // IDisposable. Best-effort dispose; real providers handle their own
            // connection pools.
            if (chatClient is IDisposable d) d.Dispose();
        }
    }

    private string LoadRoleInstructions(string kiloAgentName)
    {
        var path = Path.Combine(_kiloAgentsRoot, kiloAgentName + ".md");
        if (!File.Exists(path))
        {
            _logger.LogWarning("kilo agent file not found at {Path}; using fallback instructions", path);
            return $"You are the {kiloAgentName} agent for the PortHorizon project.";
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
        if (desc.Length == 0) desc.AppendLine($"You are the {kiloAgentName} agent for the PortHorizon project.");
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
