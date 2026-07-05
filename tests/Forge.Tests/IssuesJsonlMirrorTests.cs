using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Phase 4 of docs/embedded-issues.md: JSONL mirror of the issue
/// store. Tests cover the one-shot rewrite path; the periodic
/// BackgroundService is exercised end-to-end in the live demo
/// (orchestrator restart + tail the file).
/// </summary>
public class IssuesJsonlMirrorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _jsonlPath;
    private readonly IssueStore _store;

    public IssuesJsonlMirrorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-jsonl-{Guid.NewGuid():N}.db");
        _jsonlPath = Path.Combine(Path.GetTempPath(), $"ph-jsonl-{Guid.NewGuid():N}.jsonl");
        _store = new IssueStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        try { File.Delete(_jsonlPath); } catch { }
        try { File.Delete(_jsonlPath + ".tmp"); } catch { }
    }

    private IssuesJsonlMirror NewMirror() => new(
        _store, _jsonlPath, NullLogger<IssuesJsonlMirror>.Instance);

    [Fact]
    public async Task RewriteAsync_EmptyStore_ProducesEmptyFile()
    {
        var mirror = NewMirror();
        await mirror.RewriteAsync(default);
        Assert.True(File.Exists(_jsonlPath));
        var lines = await File.ReadAllLinesAsync(_jsonlPath);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task RewriteAsync_WithIssues_OneJsonObjectPerLine_SortedById()
    {
        await _store.CreateAsync(new NewIssue(Type: "task", Title: "z-second"));
        await _store.CreateAsync(new NewIssue(Type: "task", Title: "a-first"));
        await _store.CreateAsync(new NewIssue(Type: "epic", Title: "m-middle"));

        var mirror = NewMirror();
        await mirror.RewriteAsync(default);

        var lines = await File.ReadAllLinesAsync(_jsonlPath);
        Assert.Equal(3, lines.Length);
        // Each line is a JSON object.
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
        // Sorted by id: epic-1 < task-1 < task-2 (per-type seq).
        var ids = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { "epic-1", "task-1", "task-2" }, ids);
    }

    [Fact]
    public async Task RewriteAsync_Twice_ProducesSameFile_NoTmpLeftover()
    {
        await _store.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var mirror = NewMirror();
        await mirror.RewriteAsync(default);
        await mirror.RewriteAsync(default);

        Assert.True(File.Exists(_jsonlPath));
        Assert.False(File.Exists(_jsonlPath + ".tmp"));
    }

    [Fact]
    public async Task RewriteAsync_PreservesMetadata()
    {
        await _store.CreateAsync(new NewIssue(
            Type: "task", Title: "with meta",
            Metadata: new Dictionary<string, object>
            {
                ["branch"] = "agent/x",
                ["complex"] = 42,
            }));

        var mirror = NewMirror();
        await mirror.RewriteAsync(default);

        var line = (await File.ReadAllLinesAsync(_jsonlPath))[0];
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("agent/x", doc.RootElement.GetProperty("metadata").GetProperty("branch").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("metadata").GetProperty("complex").GetInt32());
    }

    [Fact]
    public async Task RewriteAsync_DirectoryMissing_CreatesIt()
    {
        var nested = Path.Combine(Path.GetTempPath(), $"ph-jsonl-nested-{Guid.NewGuid():N}", "sub", "issues.jsonl");
        try
        {
            var mirror = new IssuesJsonlMirror(_store, nested, NullLogger<IssuesJsonlMirror>.Instance);
            await _store.CreateAsync(new NewIssue(Type: "task", Title: "x"));
            await mirror.RewriteAsync(default);
            Assert.True(File.Exists(nested));
        }
        finally
        {
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(nested)!);
            try { Directory.Delete(dir!, recursive: true); } catch { }
        }
    }
}