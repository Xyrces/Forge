using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortHorizon.Agents.Core;

public sealed class StateStore
{
    public const int CurrentSchemaVersion = 2;

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
            var state = JsonSerializer.Deserialize<OrchestratorState>(json, _jsonOptions);
            if (state is null)
                return new OrchestratorState();
            if (state.SchemaVersion != CurrentSchemaVersion)
                throw new StateSchemaException(
                    $"State file schema version {state.SchemaVersion} is not supported " +
                    $"(expected {CurrentSchemaVersion}). Migrate or delete {filePath}.");
            return state;
        }
        catch (JsonException ex)
        {
            throw new StateCorruptException($"State file {filePath} is corrupt: {ex.Message}", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveStateAsync(OrchestratorState state, CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_statePath, "orchestrator-state.json");
        var dir = Path.GetDirectoryName(filePath)!;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(state, _jsonOptions);

            var tempPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);

            // File.Move(overwrite: true) is atomic on .NET 5+ on the same NTFS
            // volume and avoids the flaky Win32 ReplaceFile path that throws
            // "Unable to remove the file to be replaced" intermittently when
            // AV or indexer has a transient handle on the destination.
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_statePath, $"heartbeat-{heartbeat.AgentId}.json");
        var dir = Path.GetDirectoryName(filePath)!;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(heartbeat, _jsonOptions);

            var tempPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}

public sealed class StateCorruptException : Exception
{
    public StateCorruptException(string message) : base(message) { }
    public StateCorruptException(string message, Exception inner) : base(message, inner) { }
}

public sealed class StateSchemaException : Exception
{
    public StateSchemaException(string message) : base(message) { }
}

public record OrchestratorState
{
    public List<AgentTask> Tasks { get; init; }
    public DateTime LastHeartbeat { get; init; }
    public int CompletedTasks { get; set; }
    public int FailedTasks { get; set; }
    public int SchemaVersion { get; init; } = StateStore.CurrentSchemaVersion;

    public OrchestratorState(
        List<AgentTask> tasks,
        DateTime lastHeartbeat,
        int completedTasks,
        int failedTasks,
        int schemaVersion = StateStore.CurrentSchemaVersion)
    {
        Tasks = tasks;
        LastHeartbeat = lastHeartbeat;
        CompletedTasks = completedTasks;
        FailedTasks = failedTasks;
        SchemaVersion = schemaVersion;
    }

    public OrchestratorState() : this(
        new List<AgentTask>(),
        DateTime.MinValue,
        0,
        0,
        StateStore.CurrentSchemaVersion
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

public static class StateReaper
{
    public static OrchestratorState ReapStaleTasks(
        OrchestratorState state,
        TimeSpan staleAfter,
        int maxRetryCount,
        Func<string, string?>? worktreeExists)
    {
        var now = DateTime.UtcNow;
        var swept = new List<AgentTask>(state.Tasks.Count);

        foreach (var task in state.Tasks)
        {
            if (task.Status != AgentTaskStatus.InProgress)
            {
                swept.Add(task);
                continue;
            }

            var lastUpdate = task.UpdatedAt ?? task.CreatedAt;
            if (now - lastUpdate < staleAfter)
            {
                swept.Add(task);
                continue;
            }

            var retryCount = task.Parameters.GetValueOrDefault("retryCount") as int? ?? 0;
            var newStatus = retryCount >= maxRetryCount
                ? AgentTaskStatus.Failed
                : AgentTaskStatus.Pending;

            var newParams = new Dictionary<string, object>(task.Parameters, StringComparer.Ordinal)
            {
                ["retryCount"] = retryCount + 1
            };
            var reason = newStatus == AgentTaskStatus.Failed
                ? $"Reaper: stale after {staleAfter.TotalMinutes:F0}m and retry budget exhausted"
                : $"Reaper: stale after {staleAfter.TotalMinutes:F0}m, will retry";

            swept.Add(task with
            {
                Status = newStatus,
                Error = task.Error is null ? reason : $"{task.Error}; {reason}",
                Parameters = newParams
            });

            if (worktreeExists is not null)
            {
                var worktreePath = task.Parameters.GetValueOrDefault("worktreePath") as string;
                if (worktreePath is not null && worktreeExists(worktreePath) is null)
                {
                    // worktree already gone; nothing to do
                }
            }
        }

        return state with { Tasks = swept };
    }
}
