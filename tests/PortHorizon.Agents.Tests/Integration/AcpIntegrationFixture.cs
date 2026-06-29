using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.Acp;
using Xunit;

namespace PortHorizon.Agents.Tests.Integration;

/// <summary>
/// xUnit fixture that spawns a real <c>kilo acp</c> server on an ephemeral port and
/// hands out fresh <see cref="AcpClient"/> connections on demand. Skips gracefully
/// (marks tests inconclusive) if <c>kilo</c> is not on PATH.
/// </summary>
public sealed class AcpIntegrationFixture : IAsyncLifetime
{
    private const string KiloExecutable = "kilo";
    private const int WaitForBindTimeoutMs = 15_000;

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
            ?? throw new InvalidOperationException("Failed to start kilo acp server");
        _server.EnableRaisingEvents = true;

        var stderrTask = _server.StandardError.ReadToEndAsync();
        var stdoutTask = _server.StandardOutput.ReadToEndAsync();
        _server.Exited += (_, _) =>
        {
            ServerOutput =
                $"exit-code={_server.ExitCode}\n" +
                $"stdout:\n{(stdoutTask.IsCompleted ? stdoutTask.Result : "(not flushed)")}\n" +
                $"stderr:\n{stderrTask.Result}";
        };

        if (!await WaitForBindAsync(TimeSpan.FromMilliseconds(WaitForBindTimeoutMs)))
        {
            // kilo is installed and the process launched, but it never produced
            // a TCP LISTEN entry on the requested port. Possible causes:
            //   - This kilo build does not implement StreamJsonRpc-over-TCP for acp;
            //     it may use a different transport (unix socket, stdio, mDNS-only).
            //   - kilo acp on Windows defaults to a TUI/CLI interface and doesn't
            //     listen on TCP unless --mdns / an upstream consumer is connected first.
            // We capture stdout/stderr for diagnosis but do NOT mark KiloMissing —
            // the binary is present, the transport is wrong.
            try { _server.Kill(entireProcessTree: true); } catch { }
            await Task.WhenAny(stderrTask, Task.Delay(500));
            ServerBindFailed = true;
            SkipReason = $"kilo acp spawned but did not bind TCP {_port} within {WaitForBindTimeoutMs / 1000}s " +
                         $"(see ServerOutput). The kilo ACP transport on this build may not be StreamJsonRpc-over-TCP.";
        }
    }

    public async Task<AcpClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (KiloMissing || ServerBindFailed)
            throw new InvalidOperationException($"kilo acp unavailable: {SkipReason}");

        var tcp = new TcpClient { NoDelay = true };
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync(IPAddress.Loopback, _port, connectCts.Token);
        return new AcpClient(tcp, NullLogger<AcpClient>.Instance);
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

    private async Task<bool> WaitForBindAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient { NoDelay = true };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await probe.ConnectAsync(IPAddress.Loopback, _port, cts.Token);
                return true;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
        return false;
    }

    private static int PickEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private (string fileName, string args) ResolveKiloInvocation(string kiloPath)
    {
        _port = PickEphemeralPort();
        var arguments = $"acp --port {_port} --hostname 127.0.0.1 --log-level INFO";
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
