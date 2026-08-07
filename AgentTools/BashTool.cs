using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.AgentTools;

/// <summary>
/// Real <c>AIFunction</c> that runs a shell command and returns combined
/// stdout+stderr plus the exit code. Exposed to the agent alongside the
/// future read/write/git_* tools so M3 sees a structured OpenAI tool-call
/// surface instead of falling back to XML <c><tool_call></c> emission.
///
/// <para>
/// Defaults to <c>cmd.exe /c &lt;command&gt;</c> on Windows. Timeout is
/// 30 seconds; commands that exceed it are killed and a non-zero exit
/// code is returned. The runner passes the task's worktree path through
/// as <c>workingDirectory</c>; if absent, runs in <c>Environment.CurrentDirectory</c>.
/// </para>
///
/// <para>
/// NOT wrapped in <see cref="AIFunctionFactory.Create"/> directly: the
/// runner passes a delegate so we can vary <c>workingDirectory</c> per
/// task without re-instantiating the tool.
/// </para>
/// </summary>
public sealed class BashTool
{
    private readonly ILogger<BashTool>? _logger;
    private readonly string _workingDirectory;
    private readonly TimeSpan _defaultTimeout;
    private readonly IReadOnlyDictionary<string, string>? _envVars;
    private readonly Func<bool>? _mutationsAllowed;
    private readonly string? _mutationRefusalMessage;

    /// <param name="envVars">
    /// Extra environment variables injected into every spawned
    /// process — used for secrets-by-reference (FORGE_SECRET_*).
    /// Values are never logged; the model sees only the variable
    /// names in its own commands.
    /// </param>
    /// <param name="mutationsAllowed">
    /// Plan-gate hook: when set, commands classified as mutating by
    /// <see cref="ShellMutationClassifier"/> are refused until the
    /// predicate returns true (plan approved). Read/explore/build/
    /// test commands are never gated.
    /// </param>
    /// <param name="mutationRefusalMessage">
    /// Override for the refusal text returned for a gated mutating
    /// command — read-only roles (Reviewer) need their own wording;
    /// the default explains the plan-gate flow.
    /// </param>
    public BashTool(string workingDirectory, TimeSpan? defaultTimeout = null, ILogger<BashTool>? logger = null,
        IReadOnlyDictionary<string, string>? envVars = null, Func<bool>? mutationsAllowed = null,
        string? mutationRefusalMessage = null)
    {
        _workingDirectory = workingDirectory;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
        _logger = logger;
        _envVars = envVars;
        _mutationsAllowed = mutationsAllowed;
        _mutationRefusalMessage = mutationRefusalMessage;
    }

    public string WorkingDirectory => _workingDirectory;
    public TimeSpan DefaultTimeout => _defaultTimeout;

    /// <summary>
    /// Run a shell command. Returns a structured string the model can
    /// parse: <c>exit=&lt;code&gt;\nstdout:\n...\nstderr:\n...</c>.
    /// </summary>
    [Description("Run a shell command in the agent's working directory. Returns exit code + combined stdout/stderr. Use for builds, tests, file listing, git operations, etc.")]
    public async Task<string> Bash(
        [Description("The command to run. On Windows, this is passed to `cmd.exe /c`. Use `dir` instead of `ls`, `type` instead of `cat`, etc.")] string command,
        [Description("Optional working directory override. Defaults to the task's worktree.")] string? workingDirectory = null,
        [Description("Optional timeout in seconds. Defaults to 30. Max 300.")] int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "exit=-1\nstdout:\nstderr: command is required";

        // Plan gate (hard enforcement): mutating commands are
        // refused until submit_plan returns approved. Read/explore/
        // build/test commands pass ungated so the agent can ground
        // its plan in the actual repo.
        if (_mutationsAllowed is not null && !_mutationsAllowed()
            && ShellMutationClassifier.IsMutating(command, out var refusalReason))
        {
            _logger?.LogInformation("BashTool: refused mutating command pre-plan-approval: {Cmd}", command);
            // Name the reason — an unexplained refusal makes the agent
            // conclude ALL commands are blocked (observed live
            // 2026-08-06, task-382 run) and waste plan revisions.
            var why = refusalReason is null ? "" : $" (classified as: {refusalReason})";
            return _mutationRefusalMessage is null
                ? $"exit=-1\nstdout:\nstderr: REFUSED — no approved plan yet{why}. Explore the repo (read-only commands are fine), then call submit_plan with your structured plan (goal / files / approach / test / done). Mutating commands unlock after approval."
                : _mutationRefusalMessage + why;
        }

        var cwd = string.IsNullOrWhiteSpace(workingDirectory) ? _workingDirectory : workingDirectory;
        if (!Directory.Exists(cwd))
            return $"exit=-1\nstdout:\nstderr: workingDirectory '{cwd}' does not exist";

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? (int)_defaultTimeout.TotalSeconds, 1, 300));

        // P4 CI: BashTool was hardcoded to cmd.exe for the Windows
        // first-deployment. CI on ubuntu-latest needs the POSIX
        // shell instead. /bin/sh exists on macOS, Linux, and
        // WSL; on Windows the path doesn't exist but
        // OperatingSystem.IsWindows() short-circuits before we
        // get here.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }
            : new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = "-c \"" + command.Replace("\"", "\\\"") + "\"",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

        // Secrets-by-reference: inject per-project env vars
        // (FORGE_SECRET_*, GITHUB_TOKEN, ...) so the model can use
        // them in commands without the values entering its context.
        if (_envVars is not null)
        {
            foreach (var kv in _envVars)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        _logger?.LogInformation("BashTool: cwd={Cwd} cmd={Cmd} timeout={Timeout}s", cwd, command, (int)timeout.TotalSeconds);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!proc.Start())
                return "exit=-1\nstdout:\nstderr: failed to start cmd.exe";
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await proc.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                return $"exit=-1\nstdout:\n{stdout}\nstderr: timed out after {(int)timeout.TotalSeconds}s\n{stderr}";
            }
        }
        catch (Exception ex)
        {
            return $"exit=-1\nstdout:\n{stdout}\nstderr: {ex.GetType().Name}: {ex.Message}";
        }

        return $"exit={proc.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}";
    }

    /// <summary>
    /// Wrap this tool as an <see cref="AIFunction"/> for MAF. The
    /// delegate closes over the configured <c>_workingDirectory</c> so the
    /// runner can build one BashTool per task and pass it as a tool.
    /// </summary>
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        ([Description("The command to run. On Windows, this is passed to `cmd.exe /c`. Use `dir` instead of `ls`, `type` instead of `cat`, etc.")] string command,
         [Description("Optional working directory override. Defaults to the task's worktree.")] string? workingDirectory = null,
         [Description("Optional timeout in seconds. Defaults to 30. Max 300.")] int? timeoutSeconds = null)
            => Bash(command, workingDirectory, timeoutSeconds, CancellationToken.None),
        name: "bash",
        description: "Run a shell command in the agent's working directory. Returns exit code + combined stdout/stderr.");
}