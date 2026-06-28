using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortHorizon.Agents.Core;

public sealed class StateStore
{
    private readonly string _statePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public StateStore(string statePath = ".portHorizon/state")
    {
        _statePath = statePath;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        Directory.CreateDirectory(_statePath);
    }

    public async Task<OrchestratorState> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_statePath, "orchestrator-state.json");
        if (!File.Exists(filePath))
            return new OrchestratorState();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            return JsonSerializer.Deserialize<OrchestratorState>(json, _jsonOptions) ?? new OrchestratorState();
        }
        catch
        {
            return new OrchestratorState();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveStateAsync(OrchestratorState state, CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_statePath, "orchestrator-state.json");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_statePath, $"heartbeat-{heartbeat.AgentId}.json");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(heartbeat, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}

public record OrchestratorState(
    List<AgentTask> Tasks,
    Dictionary<string, AgentTaskStatus> TaskStatuses,
    Dictionary<string, string> ActiveAgents,
    DateTime LastHeartbeat,
    int CompletedTasks,
    int FailedTasks
)
{
    public OrchestratorState() : this(
        new List<AgentTask>(),
        new Dictionary<string, AgentTaskStatus>(),
        new Dictionary<string, string>(),
        DateTime.MinValue,
        0,
        0
    ) { }
}

public record AgentHeartbeat(
    string AgentId,
    AgentType AgentType,
    DateTime Timestamp,
    AgentTask? CurrentTask,
    int MemoryUsageMB,
    bool IsHealthy
);