using System.Diagnostics;

namespace PortHorizon.Agents.Core;

public sealed class AgentProcessSpawner : IDisposable
{
    private readonly Dictionary<string, Process> _processes = new();
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly int _maxConcurrent;
    private bool _disposed;

    public AgentProcessSpawner(int maxConcurrent = 2)
    {
        _maxConcurrent = maxConcurrent;
        _concurrencyLimiter = new SemaphoreSlim(maxConcurrent);
    }

    public async Task<Process> SpawnAgentAsync(
        string agentId,
        string projectPath,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        await _concurrencyLimiter.WaitAsync(cancellationToken);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" -- {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException($"Failed to start agent {agentId}");

            _processes[agentId] = process;
            return process;
        }
        catch
        {
            _concurrencyLimiter.Release();
            throw;
        }
    }

    public bool TryGetProcess(string agentId, out Process? process)
        => _processes.TryGetValue(agentId, out process);

    public void StopAgent(string agentId)
    {
        if (_processes.TryGetValue(agentId, out var process) && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    public int ActiveCount => _processes.Count(p => !p.Value.HasExited);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (id, process) in _processes)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.Dispose();
        }
        _processes.Clear();
        _concurrencyLimiter.Dispose();
    }
}