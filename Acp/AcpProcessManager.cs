using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Configuration;

namespace PortHorizon.Agents.Acp;

public sealed class AcpProcessManager : IAsyncDisposable
{
    private readonly AcpServerOptions _options;
    private readonly ILogger<AcpProcessManager> _logger;
    private readonly string _workspaceRoot;
    private Process? _process;
    private TcpClient? _tcp;
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
        var (fileName, arguments) = ResolveExecutable(
            _options.ExecutablePath,
            $"acp --port {_options.Port} --hostname {_options.Hostname} --cwd \"{_workspaceRoot}\"");

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _logger.LogInformation("Starting ACP server: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);
        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start kilo acp process.");
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) _logger.LogInformation("[kilo acp] {Line}", e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _logger.LogWarning("[kilo acp err] {Line}", e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await ConnectWithBackoffAsync(cancellationToken);
        await InitializeClientAsync(cancellationToken);
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

    private async Task ConnectWithBackoffAsync(CancellationToken cancellationToken)
    {
        var delays = new[] { 100, 250, 500, 1000, 2000, 5000 };
        Exception? last = null;
        for (var i = 0; i < delays.Length; i++)
        {
            try
            {
                var tcp = new TcpClient { NoDelay = true };
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(2));
                await tcp.ConnectAsync(_options.Hostname, _options.Port, connectCts.Token);
                _tcp = tcp;
                _client = new AcpClient(tcp);
                IsHealthy = true;
                _logger.LogInformation("Connected to ACP server at {Endpoint}", Endpoint);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogDebug(ex, "Connect attempt {Attempt} failed", i + 1);
                try { await Task.Delay(delays[i], cancellationToken); }
                catch (OperationCanceledException) { throw; }
            }
        }
        throw new InvalidOperationException($"Could not connect to ACP server at {Endpoint}: {last?.Message}", last);
    }

    private async Task InitializeClientAsync(CancellationToken cancellationToken)
    {
        var result = await _client!.InitializeAsync(
            new InitializeParams(1, new ClientCapabilities()), cancellationToken);
        _logger.LogInformation("ACP server: {Name} v{Version} (proto={Proto})",
            result.ServerName, result.ServerVersion, result.ProtocolVersion);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        IsHealthy = false;
        _logger.LogWarning("kilo acp process exited (code={Code})", _process?.ExitCode);
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        _restartAttempts++;
        var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _restartAttempts)));
        _logger.LogWarning("Restarting ACP server after {Backoff}s (attempt {N})", backoff.TotalSeconds, _restartAttempts);
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
        _tcp = null;
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
                _logger.LogWarning(ex, "Error stopping ACP process");
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
}
