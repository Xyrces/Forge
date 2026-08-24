using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// TaskModelEscalations: the single-shot llm/taskModel marker the
/// triage agent's escalate_model action writes and the dispatch path
/// consumes exactly once (no refund on run failure).
/// </summary>
public class TaskModelEscalationsTests : IDisposable
{
    private readonly string _workDir;
    private readonly MemoryStore _memory;

    public TaskModelEscalationsTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("task-escalations");
        Directory.CreateDirectory(_workDir);
        var bootstrap = new IssueStore(Path.Combine(_workDir, "memory.db"));
        bootstrap.Dispose();
        _memory = new MemoryStore(Path.Combine(_workDir, "memory.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Write_ThenPeek_True()
    {
        var markers = new TaskModelEscalations(_memory);
        Assert.False(markers.Peek("porthorizon", "task-1"));

        await markers.WriteAsync("porthorizon", "task-1", "capability-bound: sound plans rejected");
        Assert.True(markers.Peek("porthorizon", "task-1"));
        // Project scoping: another project never sees the marker.
        Assert.False(markers.Peek("forge", "task-1"));
    }

    [Fact]
    public async Task Consume_ExactlyOnce_NoRefund()
    {
        var markers = new TaskModelEscalations(_memory);
        await markers.WriteAsync("porthorizon", "task-1", "escalate note");

        var first = await markers.ConsumeAsync("porthorizon", "task-1");
        Assert.NotNull(first);
        Assert.Equal("escalate note", first!.Note);
        Assert.Equal(FailureTriageActors.Triage, first.Actor);

        Assert.False(markers.Peek("porthorizon", "task-1"));
        Assert.Null(await markers.ConsumeAsync("porthorizon", "task-1"));
    }

    [Fact]
    public async Task Write_Twice_OverwritesNote()
    {
        var markers = new TaskModelEscalations(_memory);
        await markers.WriteAsync("porthorizon", "task-1", "first");
        await markers.WriteAsync("porthorizon", "task-1", "second");

        var consumed = await markers.ConsumeAsync("porthorizon", "task-1");
        Assert.Equal("second", consumed!.Note);
    }

    [Fact]
    public async Task LoadAsync_Rehydrates_FromStore()
    {
        var first = new TaskModelEscalations(_memory);
        await first.WriteAsync("porthorizon", "task-1", "persisted note");

        // A fresh instance (post-restart) sees the persisted marker
        // only after LoadAsync.
        var second = new TaskModelEscalations(_memory);
        Assert.False(second.Peek("porthorizon", "task-1"));
        await second.LoadAsync();
        Assert.True(second.Peek("porthorizon", "task-1"));

        var consumed = await second.ConsumeAsync("porthorizon", "task-1");
        Assert.Equal("persisted note", consumed!.Note);
    }

    [Fact]
    public async Task Consume_DeletesFromStore_ReloadSeesNothing()
    {
        var first = new TaskModelEscalations(_memory);
        await first.WriteAsync("porthorizon", "task-1", "gone after consume");
        await first.ConsumeAsync("porthorizon", "task-1");

        var second = new TaskModelEscalations(_memory);
        await second.LoadAsync();
        Assert.False(second.Peek("porthorizon", "task-1"));
    }
}
