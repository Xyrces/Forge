using System.Net.Http.Json;
using System.Text.Json;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class DesignerReadScopeTests
{
    [Fact]
    public async Task EqualArtifactTimestampsSortByIdAndPreserveOwnerCopies()
    {
        await using var primary = new IssueStore(TempRoot.Instance.NewDbPath("artifact-tie-history"), "primary");
        await using var owner = new IssueStore(TempRoot.Instance.NewDbPath("artifact-tie-owner"), "owner");
        var specs = new SpecStore(owner);
        var spec = await specs.CreateAsync(new("owner", "Owned", "body"));
        var historical = new DesignArtifactStore(primary.Db);
        var local = new DesignArtifactStore(owner.Db);
        var first = "design-" + new string('0', 31) + "1";
        var second = "design-" + new string('0', 31) + "2";
        var third = "design-" + new string('0', 31) + "3";
        async Task Add(IssueStore database, DesignArtifactStore store, string id, string body)
        {
            var row = await store.CreateAsync(new(spec.Id, DesignArtifactKind.Wireframe, "Equal time", body, "markdown"));
            await using var connection = await database.Db.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE design_artifact SET id=@id,created_at='2026-11-18 00:00:00.001' WHERE id=@old";
            var replacement = command.CreateParameter(); replacement.ParameterName = "@id"; replacement.Value = id;
            var old = command.CreateParameter(); old.ParameterName = "@old"; old.Value = row.Id;
            command.Parameters.Add(replacement); command.Parameters.Add(old);
            await command.ExecuteNonQueryAsync();
        }
        // Insert in reverse ID order and place the same ID in both databases.
        await Add(owner, local, third, "current owner body");
        await Add(owner, local, first, "first body");
        await Add(primary, historical, third, "obsolete historical body");
        await Add(primary, historical, second, "unique historical body");
        await new OwnedArtifactHistoryStore(owner.Db).ImportAsync(new("primary", primary.Db, null), specs);
        var localIds = new[] { first, third };
        Assert.Equal(localIds, (await local.ListBySpecAsync(spec.Id)).Select(a => a.Id));
        Assert.Equal(localIds, (await local.ListBySpecAsync(spec.Id, DesignArtifactStatus.Draft)).Select(a => a.Id));
        Assert.Equal(localIds, (await local.ListByProjectAsync("owner")).Select(a => a.Id));
        Assert.Equal(localIds, (await local.ListBySpecsAsync([spec.Id], default)).Select(a => a.Id));
        var primarySpecs = new SpecStore(primary);
        var routing = new ProjectRoutingSpecStore(primarySpecs,
            id => id == "owner" ? specs : primarySpecs, () => [primarySpecs, specs]);
        foreach (var byProject in new[] { false, true })
        {
            var rows = await DesignerStoreRouting.ListArtifactsAsync(routing, historical,
                byProject ? "owner" : spec.Id, byProject);
            Assert.Equal(new[] { first, second, third }, rows.Select(a => a.Id));
            Assert.Equal("current owner body", rows[^1].Body);
        }
    }

    [Fact]
    public async Task ScopedHistoryPrefersThePhysicalOwnerWhenTimestampAndNumericIdCollide()
    {
        await using var primary = new IssueStore(TempRoot.Instance.NewDbPath("history-tie-primary"), "primary");
        await using var secondary = new IssueStore(TempRoot.Instance.NewDbPath("history-tie-owner"), "secondary");
        var ownerSpecs = new SpecStore(secondary);
        var spec = await ownerSpecs.CreateAsync(new NewSpec("stale-project", "Owned", "body"));
        var primaryRuns = new DesignerRunStore(primary.Db);
        var ownerRuns = new DesignerRunStore(secondary.Db);
        var historical = await primaryRuns.StartAsync(spec.Id, DesignerTriggerKind.Manual);
        var current = await ownerRuns.StartAsync(spec.Id, DesignerTriggerKind.Manual);
        Assert.Equal(historical.Id, current.Id);
        await primaryRuns.FinishAsync(historical.Id, DesignerRunStatus.LlmFailed, null, null, null, "historical", TimeSpan.Zero);
        await ownerRuns.FinishAsync(current.Id, DesignerRunStatus.Succeeded, SpecStatus.Designed, null, null, null, TimeSpan.Zero);
        foreach (var store in new[] { primary, secondary })
        {
            await using var connection = await store.Db.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE designer_run SET ts='2026-11-18 00:00:00.000'";
            await command.ExecuteNonQueryAsync();
        }
        var primarySpecs = new SpecStore(primary);
        var routing = new ProjectRoutingSpecStore(primarySpecs,
            id => id == "secondary" ? ownerSpecs : primarySpecs, () => [primarySpecs, ownerSpecs]);
        var history = await DesignerStoreRouting.ListRunsAsync(routing, primaryRuns, spec.Id, 2);
        Assert.Equal(new[] { DesignerRunStatus.Succeeded, DesignerRunStatus.LlmFailed }, history.Select(run => run.Status));
        Assert.Equal(DesignerRunStatus.Succeeded,
            Assert.Single(await DesignerStoreRouting.ListRunsAsync(routing, primaryRuns, spec.Id, 1)).Status);
    }

    [Fact]
    public async Task HistoricalListingsSurviveAControllerRestartWithANewPrimary()
    {
        await using var oldPrimary = new IssueStore(TempRoot.Instance.NewDbPath("old-primary"), "primary");
        await using var owner = new IssueStore(TempRoot.Instance.NewDbPath("history-owner"), "second");
        await using var newPrimary = new IssueStore(TempRoot.Instance.NewDbPath("new-primary"), "aaa");
        var ownerSpecs = new SpecStore(owner);
        var spec = await ownerSpecs.CreateAsync(new NewSpec("stale-field", "Owned", "body"));
        var artifact = await new DesignArtifactStore(oldPrimary.Db).CreateAsync(
            new(spec.Id, DesignArtifactKind.Wireframe, "Historical", "historical body", "html"));
        await new DesignerRunStore(oldPrimary.Db).StartAsync(spec.Id, DesignerTriggerKind.Manual);
        var stores = new Dictionary<string, ISpecStore>
        {
            ["aaa"] = new SpecStore(newPrimary), ["primary"] = new SpecStore(oldPrimary), ["second"] = ownerSpecs,
        };
        var routing = new ProjectRoutingSpecStore(stores["aaa"], id => stores.GetValueOrDefault(id), () => stores.Values.ToArray());
        await new OwnedArtifactHistoryStore(owner.Db).ImportAsync(new("primary", oldPrimary.Db, null), ownerSpecs);
        var listed = await DesignerStoreRouting.ListArtifactsAsync(routing, new DesignArtifactStore(newPrimary.Db), "second", true);
        Assert.Equal(artifact.Id, Assert.Single(listed).Id);
        var scoped = await DesignerStoreRouting.ListRunsAsync(routing, new DesignerRunStore(newPrimary.Db), spec.Id, 10);
        Assert.Equal(spec.Id, Assert.Single(scoped).SpecId);
        Assert.Empty(await DesignerStoreRouting.ListRunsAsync(routing, new DesignerRunStore(newPrimary.Db), null, 10));
        await new DesignerRunStore(oldPrimary.Db).StartAsync("orphan-spec", DesignerTriggerKind.Manual);
        Assert.Empty(await DesignerStoreRouting.ListRunsAsync(routing, new DesignerRunStore(newPrimary.Db), "orphan-spec", 10));
    }

    [Fact]
    public void ArtifactListsPreferLocalRowsAndOmitOnlyAmbiguousHistoricalIds()
    {
        var rows = ArtifactHistory.IncludeUnambiguousHistory(
            new[] { (Id: "owned", Body: "current") },
            new[] { (Id: "owned", Body: "old"), (Id: "duplicate", Body: "one"),
                (Id: "duplicate", Body: "two"), (Id: "history", Body: "unique") }, row => row.Id);
        Assert.Equal(new[] { ("owned", "current"), ("history", "unique") }, rows);
    }

    [Fact]
    public async Task EndpointsPreservePrimaryHistoryAndPhysicalProjectArtifactOwnership()
    {
        await using var primary = new IssueStore(TempRoot.Instance.NewDbPath("design-read-primary"), "primary");
        await using var secondary = new IssueStore(TempRoot.Instance.NewDbPath("design-read-secondary"), "secondary");
        var primarySpecs = new SpecStore(primary);
        var secondarySpecs = new SpecStore(secondary);
        // Deliberately stale opposite project columns: neither may override physical ownership.
        var first = await primarySpecs.CreateAsync(new NewSpec("secondary", "Primary-owned", "body"));
        var second = await secondarySpecs.CreateAsync(new NewSpec("primary", "Secondary-owned", "body"));
        var routing = new ProjectRoutingSpecStore(primarySpecs,
            id => id switch { "primary" => primarySpecs, "secondary" => secondarySpecs, _ => null },
            () => [primarySpecs, secondarySpecs]);
        var primaryRuns = new DesignerRunStore(primary.Db);
        var secondaryRuns = new DesignerRunStore(secondary.Db);
        await primaryRuns.StartAsync(first.Id, DesignerTriggerKind.Manual);
        await primaryRuns.StartAsync(first.Id, DesignerTriggerKind.Manual);
        await primaryRuns.StartAsync(second.Id, DesignerTriggerKind.Manual); // legacy primary receipt
        await secondaryRuns.StartAsync(second.Id, DesignerTriggerKind.Scheduled);
        await secondaryRuns.StartAsync(second.Id, DesignerTriggerKind.Scheduled);
        await secondaryRuns.StartAsync(second.Id, DesignerTriggerKind.Scheduled);
        var primaryArtifacts = new DesignArtifactStore(primary.Db);
        var secondaryArtifacts = new DesignArtifactStore(secondary.Db);
        Task<DesignArtifact> Add(DesignArtifactStore store, string spec, string title) =>
            store.CreateAsync(new NewDesignArtifact(spec, DesignArtifactKind.Wireframe, title, "body", "markdown"));
        var primaryArtifact = await Add(primaryArtifacts, first.Id, "primary only");
        var historical = await Add(primaryArtifacts, second.Id, "secondary historical");
        var current = await Add(secondaryArtifacts, second.Id, "secondary current");
        var duplicate = await Add(secondaryArtifacts, second.Id, "duplicated legacy receipt");
        await Add(secondaryArtifacts, "unowned-orphan-spec", "must not expand project scope");
        await using (var connection = await secondary.Db.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE design_artifact SET id=@id WHERE id=@old";
            var id = command.CreateParameter(); id.ParameterName = "@id"; id.Value = historical.Id; command.Parameters.Add(id);
            var old = command.CreateParameter(); old.ParameterName = "@old"; old.Value = duplicate.Id; command.Parameters.Add(old);
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                UPDATE design_artifact SET created_at =
                CASE WHEN id=@current THEN '2026-11-18 00:00:00.001' ELSE '2026-11-18 00:00:00.002' END
                """;
            command.Parameters.Clear();
            var currentId = command.CreateParameter(); currentId.ParameterName = "@current"; currentId.Value = current.Id;
            command.Parameters.Add(currentId);
            await command.ExecuteNonQueryAsync();
        }
        await new OwnedArtifactHistoryStore(secondary.Db).ImportAsync(new("primary", primary.Db, null), secondarySpecs);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        DesignerEndpoints.MapDesignerEndpoints(app, routing, null, primaryRuns, primaryArtifacts, NullLogger.Instance);
        DesignArtEndpoints.MapDesignArtEndpoints(app, primaryArtifacts, new ArtOutputStore(primary.Db),
            primaryRuns, null, NullLogger.Instance, routing);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{((IHost)app).GetPort()}") };
        var unscoped = await http.GetFromJsonAsync<JsonElement>("/api/designer/runs?limit=2");
        var expected = await primaryRuns.ListAsync(limit: 2);
        Assert.Equal(2, unscoped.GetArrayLength());
        Assert.Equal(expected.Select(r => (r.Id, r.SpecId)), unscoped.EnumerateArray()
            .Select(r => (r.GetProperty("id").GetInt64(), r.GetProperty("specId").GetString())));
        var scoped = await http.GetFromJsonAsync<JsonElement>($"/api/designer/runs?specId={second.Id}&limit=2");
        Assert.Equal(2, scoped.GetArrayLength());
        Assert.All(scoped.EnumerateArray(), r => Assert.Equal(second.Id, r.GetProperty("specId").GetString()));
        var missingRuns = await http.GetFromJsonAsync<JsonElement>("/api/designer/runs?specId=missing&limit=2");
        Assert.Equal(0, missingRuns.GetArrayLength());
        async Task<string[]> ArtifactIds(string path) => (await http.GetFromJsonAsync<JsonElement>(path))
            .EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(new[] { current.Id, historical.Id }, await ArtifactIds("/api/designs?projectId=secondary"));
        Assert.Equal(new[] { primaryArtifact.Id }, await ArtifactIds("/api/designs?projectId=primary"));
        Assert.Equal(new[] { current.Id, historical.Id }, await ArtifactIds($"/api/specs/{second.Id}/design-artifacts"));
        Assert.Empty(await ArtifactIds("/api/designs?projectId=missing"));
        Assert.Empty(await ArtifactIds("/api/specs/missing/design-artifacts"));
        await app.StopAsync();
    }
}
