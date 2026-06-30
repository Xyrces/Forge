using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Configuration;

namespace PortHorizon.Agents.Acp;

/// <summary>
/// Spawns `kilo serve` (kilo's HTTP-based server; kilo `acp` itself is a
/// no-op sub-mode on Windows in v7.3.54) and hands back an HttpClient
/// wrapped in <see cref="AcpClient"/>. The orchestrator treats every call
/// to <c>kilo serve</c> as a singleton: one long-lived server per orchestrator
/// instance, multiplexed by the HTTP server.
///
/// Spawning "one kilo per task" via <c>--cwd &lt;worktree&gt;</c> is still a
/// valid fallback. To enable it, swap the <see cref="StartProcessArgs"/>
/// override here.
/// </summary>
[Obsolete("Kilo path - retained for staged removal.")] public sealed class AcpProcessManager : IAsyncDisposable
{
    private readonly AcpServerOptions _options;
    private readonly ILogger<AcpProcessManager> _logger;
    private readonly string _workspaceRoot;
    private Process? _process;
    private HttpClient? _http;
    private AcpClient? _client;
    private int _restartAttempts;
    private bool _disposed;

    public bool IsHealthy { get; private set; }
    public string Endpoint => $"http://{_options.Hostname}:{_options.Port}";
    public AcpClient? Client => _client;

    public AcpProcessManager(AcpServerOptions options, string workspaceRoot, ILogger<AcpProcessManager> logger)
    {
        _options = options;
        _workspaceRoot = workspaceRoot;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AcpProcessManager));
        await StartProcessAsync(cancellationToken);
    }

    public AcpClient GetClient()
        => _client ?? throw new InvalidOperationException("ACP server is not running. Call StartAsync first.");

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        // kilo serve does not accept --cwd (only kilo acp does), so the
        // workspace root becomes the process's working directory instead.
        var (fileName, arguments) = ResolveExecutable(
            _options.ExecutablePath,
            $"serve --port {_options.Port} --hostname {_options.Hostname}");

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = _workspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _logger.LogInformation("Starting kilo server: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);
        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start kilo serve.");
        _process.EnableRaisingEvents = true;
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) _logger.LogInformation("[kilo] {Line}", e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _logger.LogInformation("[kilo] {Line}", e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForReadyAsync(cancellationToken);
        ConnectHttp();
        IsHealthy = true;
        _logger.LogInformation("kilo server ready at {Url}", Endpoint);
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var resp = await probe.GetAsync(Endpoint + "/global/health", cancellationToken);
                if ((int)resp.StatusCode < 500)
                {
                    _logger.LogDebug("kilo /global/health -> {Code}", (int)resp.StatusCode);
                    return;
                }
                last = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new InvalidOperationException($"kilo server did not become ready within 20s: {last?.Message}", last);
    }

    private void ConnectHttp()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(Endpoint + "/"),
            Timeout = TimeSpan.FromMinutes(5) // long enough for a model round-trip
        };
        _client = new AcpClient(_http);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        IsHealthy = false;
        _logger.LogWarning("kilo process exited (code={Code})", _process?.ExitCode);
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        _restartAttempts++;
        var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _restartAttempts)));
        _logger.LogWarning("Restarting kilo server after {Backoff}s (attempt {N})", backoff.TotalSeconds, _restartAttempts);
        await Task.Delay(backoff, cancellationToken);
        await StopAsync();
        await StartProcessAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        _http = null;
        IsHealthy = false;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping kilo process");
            }
            _process.Dispose();
            _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }

    private static (string FileName, string Arguments) ResolveExecutable(string configured, string args)
    {
        if (OperatingSystem.IsWindows() && !Path.HasExtension(configured))
        {
            var resolved = FindOnPath(configured);
            if (resolved is null)
                return (configured, args);
            if (resolved.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                return ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{resolved}\" {args}");
            if (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                return ("cmd.exe", $"/c \"\"{resolved}\" {args}\"");
        }
        return (configured, args);
    }

    private static string? FindOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD";
        var exts = pathext.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, exe + ext);
                if (File.Exists(candidate)) return candidate;
            }
            var direct = Path.Combine(dir, exe);
            if (File.Exists(direct)) return direct;
        }
        return null;
    }
}

