using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class ArtOutputStoreTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly ArtOutputStore _artOutputs;
    private readonly ArtistRunStore _runs;

    public ArtOutputStoreTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("art-store");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        // IssueStore owns the v10 migration (art_output + artist_run
        // tables are created in its InitializeSchema). The
        // ArtOutputStore + ArtistRunStore are just CRUD on top of
        // the same file.
        _issues = new IssueStore(_dbPath);
        _artOutputs = new ArtOutputStore(_dbPath);
        _runs = new ArtistRunStore(_dbPath);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CreateAsync_AssignsIdAndPersistsFields()
    {
        var req = new NewArtOutput(
            SpecId: "spec-1", Kind: ArtOutputKind.Mesh, Title: "Crate",
            Body: "spec-1/art-001.glb", BodyKind: "glb",
            ReferencesJson: """[{"meshyTaskId":"t-1","mode":"text-to-3d","status":"SUCCEEDED"}]""",
            Author: "artist:abc");
        var created = await _artOutputs.CreateAsync(req);
        Assert.StartsWith("art-", created.Id);
        Assert.Equal("spec-1", created.SpecId);
        Assert.Equal(ArtOutputKind.Mesh, created.Kind);
        Assert.Equal("Crate", created.Title);
        Assert.Equal("spec-1/art-001.glb", created.Body);
        Assert.Equal("glb", created.BodyKind);
        Assert.Equal(ArtOutputStatus.Draft, created.Status);
        Assert.Equal("artist:abc", created.Author);

        var fetched = await _artOutputs.GetAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task ListBySpec_ReturnsAllStatuses()
    {
        await _artOutputs.CreateAsync(new NewArtOutput("spec-1", ArtOutputKind.Mesh, "A", "spec-1/a.glb", "glb"));
        await _artOutputs.CreateAsync(new NewArtOutput("spec-1", ArtOutputKind.Texture, "B", "spec-1/b.png", "png"));
        var list = await _artOutputs.ListBySpecAsync("spec-1");
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task ListBySpec_StatusFilter()
    {
        await _artOutputs.CreateAsync(new NewArtOutput("spec-1", ArtOutputKind.Mesh, "A", "spec-1/a.glb", "glb"));
        var b = await _artOutputs.CreateAsync(new NewArtOutput("spec-1", ArtOutputKind.Texture, "B", "spec-1/b.png", "png"));
        // bump b to approved
        var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Mode=ReadWrite");
        await raw.OpenAsync();
        var cmd = raw.CreateCommand();
        cmd.CommandText = "UPDATE art_output SET status = 'approved' WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", b.Id);
        await cmd.ExecuteNonQueryAsync();
        await raw.DisposeAsync();

        var approved = await _artOutputs.ListBySpecAsync("spec-1", ArtOutputStatus.Approved);
        Assert.Single(approved);
        Assert.Equal(b.Id, approved[0].Id);

        var drafts = await _artOutputs.ListBySpecAsync("spec-1", ArtOutputStatus.Draft);
        Assert.Single(drafts);
    }

    [Fact]
    public async Task DeleteBySpec_RemovesAllRows()
    {
        await _artOutputs.CreateAsync(new NewArtOutput("spec-1", ArtOutputKind.Mesh, "A", "spec-1/a.glb", "glb"));
        await _artOutputs.CreateAsync(new NewArtOutput("spec-1", ArtOutputKind.Texture, "B", "spec-1/b.png", "png"));
        var n = await _artOutputs.DeleteBySpecAsync("spec-1");
        Assert.Equal(2, n);
        Assert.Empty(await _artOutputs.ListBySpecAsync("spec-1"));
    }

    [Fact]
    public async Task CreateAsync_RejectsBlankFields()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _artOutputs.CreateAsync(new NewArtOutput("", ArtOutputKind.Mesh, "x", "y", "glb")));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _artOutputs.CreateAsync(new NewArtOutput("s", ArtOutputKind.Mesh, "", "y", "glb")));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _artOutputs.CreateAsync(new NewArtOutput("s", ArtOutputKind.Mesh, "x", "", "glb")));
    }

    [Fact]
    public async Task ArtistRunStore_StartAndFinish_RoundTrip()
    {
        var run = await _runs.StartAsync("spec-1", ArtistTriggerKind.Manual);
        Assert.Equal(ArtistRunStatus.Started, run.Status);
        await _runs.FinishAsync(run.Id, ArtistRunStatus.Succeeded, SpecStatus.AssetReady,
            new[] { "art-a", "art-b" },
            new[] { new MeshyTaskRecord("t-1", "text-to-3d", "SUCCEEDED", "art-a", "https://x") },
            error: null,
            duration: TimeSpan.FromMilliseconds(1500));
        var list = await _runs.ListAsync(specId: "spec-1");
        Assert.Single(list);
        var fresh = list[0];
        Assert.Equal(ArtistRunStatus.Succeeded, fresh.Status);
        Assert.Equal(SpecStatus.AssetReady, fresh.NewSpecStatus);
        Assert.Equal(2, fresh.ArtOutputIds!.Count);
        Assert.Single(fresh.MeshyTasks!);
        Assert.Equal(1500, fresh.DurationMs);
    }

    [Fact]
    public async Task ArtistRunStore_ListAcrossSpecs()
    {
        await _runs.StartAsync("spec-1", ArtistTriggerKind.Scheduled);
        await _runs.StartAsync("spec-2", ArtistTriggerKind.Scheduled);
        var all = await _runs.ListAsync();
        Assert.Equal(2, all.Count);
    }
}

