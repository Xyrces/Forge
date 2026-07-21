using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class AgentStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly AgentStore _agents;

    public AgentStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-agents-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _agents = new AgentStore(_issues);
    }

    public void Dispose()
    {
        _agents.Dispose();
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Create_PersistsAndReturnsRow()
    {
        var a = await _agents.CreateAsync(new NewAgent("coredev", "CoreDev", "PortHorizon.Core"));
        Assert.False(string.IsNullOrEmpty(a.Id));
        Assert.Equal("coredev", a.AgentName);
        Assert.True(a.Enabled);
    }

    [Fact]
    public async Task List_ReturnsSortedByDisplayName()
    {
        await _agents.CreateAsync(new NewAgent("zeta", "Zeta"));
        await _agents.CreateAsync(new NewAgent("alpha", "Alpha"));
        var list = await _agents.ListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal("Alpha", list[0].DisplayName);
        Assert.Equal("Zeta", list[1].DisplayName);
    }

    [Fact]
    public async Task GetByName_FindsExisting()
    {
        await _agents.CreateAsync(new NewAgent("qa", "QA"));
        var a = await _agents.GetByNameAsync("qa");
        Assert.NotNull(a);
        Assert.Equal("qa", a!.AgentName);
    }

    [Fact]
    public async Task BulkUpsert_IsIdempotent()
    {
        await _agents.BulkUpsertFromAgentFilesAsync(new[] { ("dev", "Dev", "", (string?)null) });
        await _agents.BulkUpsertFromAgentFilesAsync(new[] { ("dev", "Dev v2", "PortHorizon.Core", (string?)null) });
        var list = await _agents.ListAsync();
        Assert.Single(list);
        Assert.Equal("Dev v2", list[0].DisplayName);
    }

    [Fact]
    public async Task Update_ChangesOnlyProvidedFields()
    {
        var a = await _agents.CreateAsync(new NewAgent("clientdev", "ClientDev", "PortHorizon.Client"));
        var updated = await _agents.UpdateAsync(a.Id, new Dictionary<string, object?>
        {
            ["enabled"] = false,
            ["scope"] = "PortHorizon.Client,PortHorizon.Tests"
        });
        Assert.False(updated.Enabled);
        Assert.Equal("PortHorizon.Client,PortHorizon.Tests", updated.Scope);
        Assert.Equal("ClientDev", updated.DisplayName);
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        var a = await _agents.CreateAsync(new NewAgent("qa", "QA"));
        await _agents.DeleteAsync(a.Id);
        var after = await _agents.GetAsync(a.Id);
        Assert.Null(after);
    }
}
