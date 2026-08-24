using System.ComponentModel;
using System.Text;
using Forge.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Forge.Agents;

/// <summary>What the triage agent decided in a run.</summary>
public sealed record TriageRunResult(bool ActionTaken, string? Action, string? Note, string? Error);

/// <summary>The triage runner seam — the production implementation runs
/// the LLM (<see cref="TriageAgent"/>); tests substitute a fake. The
/// TriageConsumer owns all deterministic guardrails and re-reads DB truth
/// before calling this.</summary>
public interface ITriageRunner
{
    Task<TriageRunResult> RunAsync(
        string taskId, string signature, string classification, CancellationToken ct = default);
}

/// <summary>
/// The failure-triage agent (phase 2). Pipeline-side role with its own
/// <see cref="AgentType.Triage"/> (independently editable model via the
/// standard override stack); no territory, no sprint membership, no bash
/// or worktree — the ONLY things it can do are the bounded actions
/// in <see cref="TriageTools"/>. Runs on the TriageRequested event, never
/// on a poller.
/// </summary>
public sealed class TriageAgent : ITriageRunner
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly IIssueStore _issues;
    private readonly FailureTriageStore _triage;
    private readonly TaskStateMachine? _lifecycle;
    private readonly TriageEscalationContext? _escalation;
    private readonly string? _projectRoot;
    private readonly string _projectId;
    private readonly ILogger<TriageAgent> _logger;

    public TriageAgent(
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        IIssueStore issues,
        FailureTriageStore triage,
        TaskStateMachine? lifecycle,
        string projectId,
        string? projectRoot,
        ILogger<TriageAgent> logger,
        TriageEscalationContext? escalation = null)
    {
        _chatClientFactory = chatClientFactory;
        _config = config;
        _issues = issues;
        _triage = triage;
        _lifecycle = lifecycle;
        _projectId = projectId;
        _projectRoot = projectRoot;
        _logger = logger;
        _escalation = escalation;
    }

    public async Task<TriageRunResult> RunAsync(
        string taskId, string signature, string classification, CancellationToken ct = default)
    {
        var task = await _issues.GetAsync(taskId, ct);
        if (task is null) return new TriageRunResult(false, null, null, $"task {taskId} not found");
        var history = await _triage.ListForTaskAsync(taskId, ct);

        var tools = new TriageTools(_issues, _triage, _lifecycle, _logger, _escalation);
        string? actionTaken = null;
        string? actionNote = null;

        var requeueTool = AIFunctionFactory.Create(
            async ([Description("The reorientation for the next run — cites the SPECIFIC failure evidence (error text, failing command, rejected plan element, review comment) and says what to do differently. Never 'try again'.")] string note,
                   [Description("Optional supporting context quoted from the evidence (error excerpt, command output).")] string? context = null) =>
            {
                var result = await tools.RequeueWithGuidanceAsync(taskId, signature, note, context, ct);
                if (result.StartsWith("ok:", StringComparison.Ordinal)) { actionTaken = "requeue"; actionNote = note; }
                return result;
            },
            name: "requeue_with_guidance",
            description: "Requeue the failed task with an evidence-cited reorientation that rides the next run's prompt. Spends one of the task's strike rounds.");

        var parkTool = AIFunctionFactory.Create(
            async ([Description("Why a human must decide — what evidence made this a judgment call.")] string reason) =>
            {
                var result = await tools.ParkForOperatorAsync(taskId, reason, ct);
                if (result.StartsWith("ok:", StringComparison.Ordinal)) { actionTaken = "parked"; actionNote = reason; }
                return result;
            },
            name: "park_for_operator",
            description: "Park the task Failed/Blocked for the operator. The right call for judgment calls, ambiguous evidence, and capability-bound failures. Always safe.");

        var flagTool = AIFunctionFactory.Create(
            async ([Description("The bug-suspect signature (usually the current failure signature).")] string suspectSignature,
                   [Description("The evidence that points at a product bug rather than a process failure — cite file:line or error text.")] string evidence) =>
            {
                var result = await tools.FlagBugSuspectAsync(taskId, suspectSignature, evidence, ct);
                if (result.StartsWith("ok:", StringComparison.Ordinal)) { actionTaken = "flag-bug"; actionNote = evidence; }
                return result;
            },
            name: "flag_bug_suspect",
            description: "Flag this failure signature as a suspected PRODUCT BUG. Ledger flag only — never creates an issue, never edits code.");

        var escalateTool = AIFunctionFactory.Create(
            async ([Description("Why the evidence says this failure is CAPABILITY-BOUND — cite the specific signal (e.g. repeated plan rejections of sound plans, a complex multi-file change collapsing) and why a stronger model is the fix. Never 'try harder'.")] string note) =>
            {
                var result = await tools.EscalateModelAsync(taskId, signature, note, ct);
                if (result.StartsWith("ok:", StringComparison.Ordinal)) { actionTaken = "escalate"; actionNote = note; }
                return result;
            },
            name: "escalate_model",
            description: "Requeue the task so its next dev run rides the role's configured ESCALATION model (a stronger model chosen by the operator, never by you). For capability-bound failures only — process failures get requeue/park/flag. Spends one of the task's strike rounds.");

        var chatClient = _chatClientFactory.Create(_config, AgentType.Triage, _projectId);
        chatClient = new ChatClientBuilder(chatClient).UseFunctionInvocation().Build();
        var agent = new ChatClientAgent(
            chatClient,
            instructions: LoadInstructions(),
            name: "triage",
            description: $"Triage agent for task {taskId}",
            tools: new List<AITool> { requeueTool, parkTool, flagTool, escalateTool });

        var userMessage = new ChatMessage(ChatRole.User, BuildUserMessage(task, signature, classification, history));
        try
        {
            var response = await agent.RunAsync(userMessage, cancellationToken: ct);
            _logger.LogInformation(
                "Triage run for {TaskId} ({Signature}): action={Action} note={Note} reply={Reply}",
                taskId, signature, actionTaken ?? "none", actionNote ?? "-",
                response.Text is { Length: > 200 } ? response.Text[..200] : response.Text);
            return new TriageRunResult(actionTaken is not null, actionTaken, actionNote, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A triage LLM failure must never break the failure pipeline
            // — the open ledger row simply stays open for the operator.
            _logger.LogWarning(ex, "Triage run for {TaskId} failed — row stays open for the operator", taskId);
            return new TriageRunResult(false, null, null, ex.Message);
        }
    }

    private string BuildUserMessage(
        IssueRecord task, string signature, string classification,
        IReadOnlyList<FailureTriageEntry> history)
    {
        var sb = new StringBuilder();
        sb.Append("A task just entered ").Append(task.Status).AppendLine(". Read the evidence and take exactly one action.");
        sb.Append("\nTask: ").Append(task.Id).Append(" — ").AppendLine(task.Title);
        if (!string.IsNullOrWhiteSpace(task.Description))
            sb.Append("Description:\n```\n").AppendLine(task.Description).AppendLine("```");
        sb.Append("\nClassifier signature: ").Append(signature)
            .Append(" (").Append(classification).AppendLine(")");
        var error = task.GetMetadata("lastError");
        if (!string.IsNullOrWhiteSpace(error))
            sb.Append("\nFreshest error excerpt:\n```\n").AppendLine(error.Length > 800 ? error[..800] : error).AppendLine("```");
        if (history.Count > 0)
        {
            sb.AppendLine("\nLedger history for this task (newest first):");
            foreach (var h in history.Take(5))
            {
                sb.Append("- ").Append(h.FailedAt.ToString("yyyy-MM-dd HH:mm")).Append(" UTC — ")
                    .Append(h.Signature).Append(" — action: ").Append(h.Action ?? "none")
                    .Append(" (").Append(h.Actor ?? "-").Append("), outcome: ").AppendLine(h.Outcome ?? "-");
            }
        }
        sb.AppendLine("\nTake exactly one action now.");
        return sb.ToString();
    }

    /// <summary>Full markdown body of agents/triage.md (frontmatter
    /// stripped); the per-project override (&lt;projectRoot&gt;/agents)
    /// wins over the built-in copy that ships next to the app.</summary>
    private string LoadInstructions()
    {
        const string fallback = "You are the triage agent. Take exactly one action: requeue_with_guidance, park_for_operator, flag_bug_suspect, or escalate_model.";
        try
        {
            var root = RolePromptRoot.Resolve(_projectRoot ?? AppContext.BaseDirectory);
            var path = Path.Combine(root, "triage.md");
            if (!File.Exists(path))
            {
                _logger.LogWarning("triage role prompt not found at {Path}; using minimal instructions", path);
                return fallback;
            }
            var lines = File.ReadAllText(path).Split('\n');
            // Strip the YAML frontmatter fence; the body is the prompt.
            if (lines.Length > 0 && lines[0].TrimEnd('\r') == "---")
            {
                var end = Array.FindIndex(lines, 1, l => l.TrimEnd('\r') == "---");
                if (end > 0) return string.Join('\n', lines.Skip(end + 1)).Trim();
            }
            return string.Join('\n', lines).Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load triage role prompt; using minimal instructions");
            return fallback;
        }
    }
}
