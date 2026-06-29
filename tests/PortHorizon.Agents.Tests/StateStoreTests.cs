using System.Text.Json;
using PortHorizon.Agents.Core;
using Xunit;

namespace PortHorizon.Agents.Tests;

public class StateStoreTests : IDisposable
{
    private readonly string _tempDir;

    public StateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ph-state-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RoundTrip_PreservesTasks()
    {
        var store = new StateStore(_tempDir);
        var state = new OrchestratorState();
        state.Tasks.Add(new AgentTask(
            Id: "t-1", Type: "ecs", Description: "do thing",
            Parameters: new Dictionary<string, object> { ["retryCount"] = 0 },
            Branch: "agent/t-1", Status: AgentTaskStatus.Pending,
            Error: null, CreatedAt: DateTime.UtcNow));
        await store.SaveStateAsync(state);
        var loaded = await store.LoadStateAsync();
        Assert.Single(loaded.Tasks);
        Assert.Equal("t-1", loaded.Tasks[0].Id);
        Assert.Equal(AgentTaskStatus.Pending, loaded.Tasks[0].Status);
        Assert.Equal("agent/t-1", loaded.Tasks[0].Branch);
        Assert.Equal("ecs", loaded.Tasks[0].Type);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsEmptyState()
    {
        var store = new StateStore(_tempDir);
        var state = await store.LoadStateAsync();
        Assert.Empty(state.Tasks);
        Assert.Equal(StateStore.CurrentSchemaVersion, state.SchemaVersion);
    }

    [Fact]
    public async Task Load_CorruptFile_ThrowsTypedException()
    {
        var store = new StateStore(_tempDir);
        var filePath = Path.Combine(_tempDir, "orchestrator-state.json");
        await File.WriteAllTextAsync(filePath, "{ this is not json");
        await Assert.ThrowsAsync<StateCorruptException>(() => store.LoadStateAsync());
    }

    [Fact]
    public async Task Load_WrongSchemaVersion_ThrowsSchemaException()
    {
        var store = new StateStore(_tempDir);
        var filePath = Path.Combine(_tempDir, "orchestrator-state.json");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new { schemaVersion = 1, tasks = Array.Empty<object>() }));
        var ex = await Assert.ThrowsAsync<StateSchemaException>(() => store.LoadStateAsync());
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public async Task SaveAtomic_TempFileNotLeftBehind()
    {
        var store = new StateStore(_tempDir);
        var state = new OrchestratorState();
        state.Tasks.Add(new AgentTask("t", "ecs", "d", new Dictionary<string, object>(), "agent/t"));
        await store.SaveStateAsync(state);
        var leftovers = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(leftovers);
        Assert.True(File.Exists(Path.Combine(_tempDir, "orchestrator-state.json")));
    }
}