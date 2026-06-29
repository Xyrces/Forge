using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.Acp;
using Xunit;

namespace PortHorizon.Agents.Tests.Integration;

/// <summary>
/// xUnit fixture that spawns a real <c>kilo serve</c> on an ephemeral port and
/// hands out fresh <see cref="AcpClient"/> connections on demand. Skips gracefully
/// if <c>kilo</c> is not on PATH or if the server fails to bind.
/// </summary>
public sealed class AcpIntegrationFixture : IAsyncLifetime
{
    private const string KiloExecutable = "kilo";
    private const int WaitForReadyTimeoutMs = 25_000;

    private Process? _server;
    private string? _kiloPath;
    private int _port;

    public int Port => _port;
    public string Endpoint => $"http://127.0.0.1:{_port}";
    public bool KiloMissing { get; private set; }
    public bool ServerBindFailed { get; private set; }
    public string? SkipReason { get; private set; }
    public string? ServerOutput { get; private set; }
    public string? LastProbeResult { get; set; }

    public async Task InitializeAsync()
    {
        _kiloPath = FindOnPath(KiloExecutable);
        if (_kiloPath is null)
        {
            KiloMissing = true;
            SkipReason = $"'{KiloExecutable}' not found on PATH.";
            return;
        }

        _port = PickEphemeralPort();
        var (fileName, args) = ResolveKiloInvocation(_kiloPath);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _server = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start kilo serve");
        _server.EnableRaisingEvents = true;

        var stdoutTask = _server.StandardOutput.ReadToEndAsync();
        var stderrTask = _server.StandardError.ReadToEndAsync();
        _server.Exited += (_, _) =>
        {
            ServerOutput =
                $"exit-code={_server.ExitCode}\n" +
                $"stdout (truncated):\n{(stdoutTask.IsCompleted ? stdoutTask.Result : "(not flushed)")}\n" +
                $"stderr (truncated):\n{(stderrTask.IsCompleted ? stderrTask.Result.Substring(0, Math.Min(800, stderrTask.Result.Length)) : "(not flushed)")}";
        };

        if (!await WaitForReadyAsync(TimeSpan.FromMilliseconds(WaitForReadyTimeoutMs)))
        {
            try { _server.Kill(entireProcessTree: true); } catch { }
            ServerBindFailed = true;
            SkipReason = $"kilo serve started but did not become healthy at {Endpoint} within {WaitForReadyTimeoutMs / 1000}s. " +
                         $"ServerOutput: {ServerOutput}";
        }
    }

    public async Task<AcpClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (KiloMissing || ServerBindFailed)
            throw new InvalidOperationException($"kilo serve unavailable: {SkipReason}");

        var http = new HttpClient
        {
            BaseAddress = new Uri(Endpoint + "/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        return new AcpClient(http, NullLogger<AcpClient>.Instance);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (_server is not null && !_server.HasExited)
            {
                _server.Kill(entireProcessTree: true);
                _server.WaitForExit(3000);
            }
            _server?.Dispose();
        }
        catch { /* swallow during teardown */ }
        return Task.CompletedTask;
    }

    private async Task<bool> WaitForReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await probe.GetAsync(Endpoint + "/global/health");
                // kilo v7.3.54 returns 200 once ready (sometimes 404 if /global/health doesn't exist);
                // any non-5xx response means the server is accepting connections.
                if ((int)resp.StatusCode < 500) return true;
            }
            catch { /* not yet */ }
            await Task.Delay(250);
        }
        return false;
    }

    private static int PickEphemeralPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private (string fileName, string args) ResolveKiloInvocation(string kiloPath)
    {
        // kilo serve does NOT accept --cwd (only kilo acp does). The CWD
        // for the spawned process is inherited from the parent shell.
        var arguments = $"serve --port {_port} --hostname 127.0.0.1 --log-level INFO";
        if (kiloPath.EndsWith(".CMD", StringComparison.OrdinalIgnoreCase)
            || kiloPath.EndsWith(".BAT", StringComparison.OrdinalIgnoreCase))
        {
            return ("cmd.exe", $"/c \"\"{kiloPath}\" {arguments}\"");
        }
        if (kiloPath.EndsWith(".PS1", StringComparison.OrdinalIgnoreCase))
        {
            return ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{kiloPath}\" {arguments}");
        }
        return (kiloPath, arguments);
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
