using Forge.Core;
using Forge.Core.Db;
using Xunit;

namespace Forge.Tests;

public sealed class OwnedArtifactHistoryStoreTests
{
    [Fact]
    public async Task ImportedRecordsSurviveWriterRemovalAndKeepOnlyPhysicallyOwnedSpecs()
    {
        var path = TempRoot.Instance.NewDbPath("history-owner");
        await using var owner = new IssueStore(path, "owner");
        var specs = new SpecStore(owner);
        var spec = await specs.CreateAsync(new("stale-project", "Owned", "spec"));
        var history = new OwnedArtifactHistoryStore(owner.Db);
        string designId, artId;
        await using (var writer = new IssueStore(TempRoot.Instance.NewDbPath("history-writer"), "writer"))
        {
            var design = await new DesignArtifactStore(writer.Db).CreateAsync(new(spec.Id,
                DesignArtifactKind.Wireframe, "Design", "full design body", "markdown"));
            var art = await new ArtOutputStore(writer.Db).CreateAsync(new(spec.Id,
                ArtOutputKind.Mesh, "Mesh", spec.Id + "/mesh.glb", "glb"));
            await new DesignArtifactStore(writer.Db).CreateAsync(new("foreign-spec",
                DesignArtifactKind.Wireframe, "Foreign", "private", "markdown"));
            var source = new ArtifactStoreSource("writer", writer.Db, "/writer/art-output");
            Assert.Equal(2, await history.ImportAsync(source, specs));
            Assert.Equal(2, await history.ImportAsync(source, specs));
            designId = design.Id;
            artId = art.Id;
        }
        await using var restarted = new IssueStore(path, "owner");
        var restored = new OwnedArtifactHistoryStore(restarted.Db);
        var found = Assert.IsType<OwnedHistoricalArtifact<DesignArtifact>>(await restored.GetDesignAsync(designId));
        Assert.Equal("full design body", found.Artifact.Body);
        Assert.Equal("writer", found.WriterProjectId);
        var mesh = Assert.IsType<OwnedHistoricalArtifact<ArtOutput>>(await restored.GetArtAsync(artId));
        Assert.Equal(spec.Id + "/mesh.glb", mesh.Artifact.Body);
        Assert.Equal("/writer/art-output", mesh.ArtOutputRoot);
        Assert.Equal(2, (await restored.ListReferencesAsync(spec.Id)).Count);
        Assert.Single(await restored.ListDesignsAsync([spec.Id, "foreign-spec"]));
        Assert.Equal(artId, Assert.Single(await restored.ListArtAsync([spec.Id, "foreign-spec"])).Id);
        Assert.Empty(await restored.ListReferencesAsync("foreign-spec"));
        await using var connection = await owner.Db.OpenAsync();
        await using var remove = connection.CreateCommand();
        remove.CommandText = "DELETE FROM spec WHERE id=@id";
        remove.AddParam("@id", spec.Id);
        await remove.ExecuteNonQueryAsync();
        Assert.Null(await restored.GetDesignAsync(designId));
        Assert.Null(await restored.GetArtAsync(artId));
        Assert.Empty(await restored.ListDesignsAsync([spec.Id]));
        Assert.Empty(await restored.ListArtAsync([spec.Id]));
        Assert.Empty(await restored.ListReferencesAsync(spec.Id));
    }

    [Fact]
    public async Task LaterImportsPreserveRemovedWritersAndDoNotChooseAnAmbiguousId()
    {
        await using var owner = new IssueStore(TempRoot.Instance.NewDbPath("history-many-owner"), "owner");
        await using var first = new IssueStore(TempRoot.Instance.NewDbPath("history-first"), "first");
        await using var second = new IssueStore(TempRoot.Instance.NewDbPath("history-second"), "second");
        var specs = new SpecStore(owner);
        var spec = await specs.CreateAsync(new("owner", "Owned", "spec"));
        var history = new OwnedArtifactHistoryStore(owner.Db);
        var original = await new DesignArtifactStore(first.Db).CreateAsync(new(spec.Id,
            DesignArtifactKind.Wireframe, "Original", "first body", "markdown"));
        await history.ImportAsync(new("first", first.Db, "/first/art"), specs);
        Assert.Equal(0, await history.ImportAsync(new("second", second.Db, "/second/art"), specs));
        Assert.Equal("first body", (await history.GetDesignAsync(original.Id))!.Artifact.Body);
        var anotherSpec = await specs.CreateAsync(new("owner", "Another owned spec", "body"));
        var duplicate = await new DesignArtifactStore(second.Db).CreateAsync(new(anotherSpec.Id,
            DesignArtifactKind.Wireframe, "Conflicting", "second body", "markdown"));
        await using (var connection = await second.Db.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE design_artifact SET id=@new WHERE id=@old";
            command.AddParam("@new", original.Id);
            command.AddParam("@old", duplicate.Id);
            await command.ExecuteNonQueryAsync();
        }
        var independent = await new DesignArtifactStore(second.Db).CreateAsync(new(spec.Id,
            DesignArtifactKind.Wireframe, "Unique", "unique body", "markdown"));
        await history.ImportAsync(new("second", second.Db, "/second/art"), specs);
        await Assert.ThrowsAsync<ArtifactHistoryAmbiguousException>(() => history.GetDesignAsync(original.Id));
        Assert.Equal(independent.Id, Assert.Single(await history.ListReferencesAsync(spec.Id)).Id);
        Assert.Equal(independent.Id, Assert.Single(await history.ListDesignsAsync([spec.Id])).Id);
    }

    [Fact]
    public async Task BackfillCrossesTheSpecBatchBoundaryAndRejectsADifferentOwner()
    {
        await using var owner = new IssueStore(TempRoot.Instance.NewDbPath("history-batch-owner"), "owner");
        await using var writer = new IssueStore(TempRoot.Instance.NewDbPath("history-batch-writer"), "writer");
        var specs = new SpecStore(owner);
        var ids = new List<string>();
        for (var i = 0; i < 101; i++)
        {
            var spec = await specs.CreateAsync(new("owner", $"Owned {i}", "spec"));
            ids.Add(spec.Id);
            await new DesignArtifactStore(writer.Db).CreateAsync(new(spec.Id,
                DesignArtifactKind.Wireframe, "Design", "body", "markdown"));
        }
        var history = new OwnedArtifactHistoryStore(owner.Db);
        Assert.Equal(101, await history.ImportAsync(new("writer", writer.Db, null), specs));
        Assert.Equal(101, (await history.ListDesignsAsync(ids)).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => history.ImportAsync(new("writer", writer.Db, null), new SpecStore(writer)));
        Assert.Equal(0, await history.ImportAsync(new("owner", owner.Db, null), specs));
    }

    [Fact]
    public async Task UpgradeFrom42CreatesOwnedHistoryAndPreservesExistingWork()
    {
        var path = TempRoot.Instance.NewDbPath("history-upgrade");
        string issueId;
        await using (var issues = new IssueStore(path))
        {
            issueId = (await issues.CreateAsync(new("task", "Preserved"))).Id;
            await using var connection = await issues.Db.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE owned_artifact_history; DELETE FROM schema_version WHERE version>42";
            await command.ExecuteNonQueryAsync();
        }
        await using var upgraded = new IssueStore(path);
        Assert.Equal("Preserved", (await upgraded.GetAsync(issueId))!.Title);
        await using var check = await upgraded.Db.OpenAsync();
        M043OwnedArtifactHistory.Create(check, SqliteDialect.Instance);
        M043OwnedArtifactHistory.Create(check, SqliteDialect.Instance);
        await using var read = check.CreateCommand();
        read.CommandText = "SELECT MAX(version) FROM schema_version";
        Assert.Equal(43L, Convert.ToInt64(await read.ExecuteScalarAsync()));
        read.CommandText = "SELECT COUNT(*) FROM owned_artifact_history";
        Assert.Equal(0L, Convert.ToInt64(await read.ExecuteScalarAsync()));
        var migration = Assert.IsType<M043OwnedArtifactHistory>(SqlServerMigrations.All.Single(m => m.Version == 43));
        using var unopened = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        migration.Up(unopened, new SqlServerDialect("core"), ForgeSchemaProfile.Core);
        Assert.Equal(System.Data.ConnectionState.Closed, unopened.State);
    }
}
