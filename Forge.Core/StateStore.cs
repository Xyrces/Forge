using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core;

/// <summary>
/// Slim JSON store for orchestrator heartbeats + counters. The
/// actual task queue lives in <see cref="IssueStore"/> (SQLite,
/// schema v7); this file is a viewer artifact so dashboards that
/// previously read <c>orchestrator-state.json</c> still see
/// heartbeat + counters without a code change.
/// </summary>
public sealed class StateStore
{
    public const int CurrentSchemaVersion = 3;

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

/// <summary>
/// Heartbeat + counters. Phase 5 of docs/embedded-issues.md: tasks
/// live in <see cref="IssueStore"/> now; this record only carries
/// the operator-visible rollups.
/// </summary>
public record OrchestratorState
{
    public DateTime LastHeartbeat { get; init; }
    public int CompletedTasks { get; set; }
    public int FailedTasks { get; set; }
    public int SchemaVersion { get; init; } = StateStore.CurrentSchemaVersion;

    public OrchestratorState(
        DateTime lastHeartbeat,
        int completedTasks,
        int failedTasks,
        int schemaVersion = StateStore.CurrentSchemaVersion)
    {
        LastHeartbeat = lastHeartbeat;
        CompletedTasks = completedTasks;
        FailedTasks = failedTasks;
        SchemaVersion = schemaVersion;
    }

    public OrchestratorState() : this(
        DateTime.MinValue,
        0,
        0,
        StateStore.CurrentSchemaVersion
    ) { }
}
