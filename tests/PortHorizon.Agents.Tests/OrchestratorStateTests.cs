using PortHorizon.Agents.Core;
using Xunit;

namespace PortHorizon.Agents.Tests;

/// <summary>
/// Phase 5 of docs/embedded-issues.md: StateStore shed its Task list
/// (which moved to IssueStore) and bumped its schema version 2 -> 3.
/// These tests cover the slimmed-down surface: heartbeat + counters +
/// load/save roundtrip with the new schema.
/// </summary>
public class OrchestratorStateTests : IDisposable
{
    private readonly string _dir;
    private readonly StateStore _store;

    public OrchestratorStateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ph-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new StateStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void CurrentSchemaVersion_Is3()
    {
        Assert.Equal(3, StateStore.CurrentSchemaVersion);
    }

    [Fact]
    public void DefaultState_HasExpectedCounterShape()
    {
        var state = new OrchestratorState();
        Assert.Equal(DateTime.MinValue, state.LastHeartbeat);
        Assert.Equal(0, state.CompletedTasks);
        Assert.Equal(0, state.FailedTasks);
        Assert.Equal(3, state.SchemaVersion);
    }

    [Fact]
    public async Task Save_Then_Load_RoundtripsCounters()
    {
        var saved = new OrchestratorState(
            lastHeartbeat: DateTime.UtcNow,
            completedTasks: 7,
            failedTasks: 2);
        await _store.SaveStateAsync(saved);

        var loaded = await _store.LoadStateAsync();
        Assert.Equal(7, loaded.CompletedTasks);
        Assert.Equal(2, loaded.FailedTasks);
        Assert.Equal(3, loaded.SchemaVersion);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsDefault()
    {
        // Fresh directory, no state file written.
        var dir2 = Path.Combine(Path.GetTempPath(), $"ph-state-empty-{Guid.NewGuid():N}");
        try
        {
            var s = new StateStore(dir2);
            var state = await s.LoadStateAsync();
            Assert.Equal(0, state.CompletedTasks);
            Assert.Equal(0, state.FailedTasks);
        }
        finally
        {
            try { Directory.Delete(dir2, recursive: true); } catch { }
        }
    }
}