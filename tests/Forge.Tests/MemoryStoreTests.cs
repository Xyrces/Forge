using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Phase 3 of docs/embedded-issues.md: memory table + the bd
/// remember / prime analog. Tests cover CRUD, TTL filtering, prefix
/// matching, and the prompt-rendering helper.
/// </summary>
public class MemoryStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MemoryStore _store;

    public MemoryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-mem-{Guid.NewGuid():N}.db");
        // Initialize the schema via IssueStore so the memory table
        // exists (IssueStore.InitializeSchema creates it as part of
        // its v7 block; we then point MemoryStore at the same file).
        // The IssueStore ctor runs InitializeSchema synchronously; we
        // just need its side effect, so construct + drop the reference.
        _ = new IssueStore(_dbPath);
        _store = new MemoryStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Remember_Then_Recall_RoundTrips()
    {
        await _store.RememberAsync("coding-style/no-linq-in-hot-paths", "Avoid LINQ in inner loops.");
        var list = await _store.RecallAsync();
        Assert.Single(list);
        Assert.Equal("coding-style/no-linq-in-hot-paths", list[0].Key);
        Assert.Equal("Avoid LINQ in inner loops.", list[0].Body);
        Assert.Null(list[0].ExpiresAt);
    }

    [Fact]
    public async Task Remember_SameKey_Upserts()
    {
        await _store.RememberAsync("foo", "first");
        await _store.RememberAsync("foo", "second");
        var list = await _store.RecallAsync();
        Assert.Single(list);
        Assert.Equal("second", list[0].Body);
    }

    [Fact]
    public async Task Remember_WithTtl_SetsExpiresAt()
    {
        await _store.RememberAsync("short-lived", "expires soon", ttlDays: 7);
        var list = await _store.RecallAsync();
        Assert.Single(list);
        Assert.Equal(7, list[0].TtlDays);
        Assert.NotNull(list[0].ExpiresAt);
        Assert.True(list[0].ExpiresAt!.Value > DateTime.UtcNow);
        Assert.True(list[0].ExpiresAt.Value < DateTime.UtcNow.AddDays(8));
    }

    [Fact]
    public async Task Recall_Prefix_Filters()
    {
        await _store.RememberAsync("coding-style/no-linq", "x");
        await _store.RememberAsync("coding-style/no-alloc", "y");
        await _store.RememberAsync("ops/dont-rm-rf", "z");
        var list = await _store.RecallAsync("coding-style/");
        Assert.Equal(2, list.Count);
        Assert.All(list, m => Assert.StartsWith("coding-style/", m.Key));
    }

    [Fact]
    public async Task Recall_NoPrefix_ReturnsAll()
    {
        await _store.RememberAsync("a", "1");
        await _store.RememberAsync("b", "2");
        await _store.RememberAsync("c", "3");
        var list = await _store.RecallAsync();
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public async Task Forget_Existing_ReturnsTrue()
    {
        await _store.RememberAsync("x", "x");
        Assert.True(await _store.ForgetAsync("x"));
        Assert.Empty(await _store.RecallAsync());
    }

    [Fact]
    public async Task Forget_Missing_ReturnsFalse()
    {
        Assert.False(await _store.ForgetAsync("never-stored"));
    }

    [Fact]
    public async Task Remember_EmptyKey_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.RememberAsync("", "body"));
    }

    [Fact]
    public async Task Remember_NullBody_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _store.RememberAsync("k", null!));
    }

    [Fact]
    public void RenderForPrompt_NoMemories_ReturnsEmptyString()
    {
        var s = MemoryStore.RenderForPrompt(Array.Empty<MemoryRecord>());
        Assert.Equal(string.Empty, s);
    }

    [Fact]
    public void RenderForPrompt_WithMemories_ContainsKeysAndBodies()
    {
        var list = new List<MemoryRecord>
        {
            new(0, "k1", "body1", DateTime.UtcNow, null, null),
            new(0, "k2", "body2", DateTime.UtcNow, 7, DateTime.UtcNow.AddDays(7)),
        };
        var rendered = MemoryStore.RenderForPrompt(list);
        Assert.Contains("## Project memory", rendered);
        Assert.Contains("**k1**", rendered);
        Assert.Contains("**k2**", rendered);
        Assert.Contains("body1", rendered);
        Assert.Contains("body2", rendered);
        Assert.Contains("expires", rendered); // TTL rendered
    }

    [Fact]
    public async Task SchemaV7_MemoryTableExists()
    {
        // Sanity: the IssueStore bootstrap above created the memory
        // table. Confirm by writing then reading.
        await _store.RememberAsync("schema-check", "ok");
        Assert.NotEmpty(await _store.RecallAsync());
    }
}