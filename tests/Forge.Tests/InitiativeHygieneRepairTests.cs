using Forge.Tests.Integration;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Core.Initiatives;
using Xunit;

namespace Forge.Tests;

public sealed class InitiativeHygieneRepairTests
{
    private static async Task<(IssueStore Issues, InitiativeProposal Proposal, InitiativeIngestion Receipt, HygieneRepairRequest Request)> Seed()
    {
        var issues = new IssueStore(TempRoot.Instance.NewDbPath("hygiene-repair"), "test");
        var proposals = new InitiativeProposalStore(issues);
        var session = await new IntakeStore(issues).CreateAsync("test", "scope");
        var draft = await proposals.CreateAsync(new("test", "source", "user", "scope", "approved body", SessionId: session.Id));
        var proposal = await proposals.ApproveAsync("test", draft.Id, 1, draft.Hash, "operator");
        var ingestion = new InitiativeIngestionStore(issues);
        var receipt = await ingestion.IngestAsync(proposal);
        Assert.True(await ingestion.TryBeginRefinementAsync(proposal.Id, 1));
        Assert.True(await ingestion.CompleteRefinementAsync(proposal.Id, 1));
        var runs = new DesignerRunStore(issues.Db);
        var run = await runs.StartAsync(receipt.SpecId, DesignerTriggerKind.Scheduled);
        await runs.FinishAsync(run.Id, DesignerRunStatus.HygieneFailed, null, null,
            "{\"Passed\":false,\"Findings\":[{\"Message\":\"epic-1 is not a specification\"}]}", "Invalid dependency", TimeSpan.Zero);
        return (issues, proposal, receipt, new(receipt.SpecId, 1, run.Id));
    }

    [Fact]
    public async Task ExplicitRepairPreservesScopeHistoryAndQueuesDiagnosticAwareRefinement()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var store = new InitiativeIngestionStore(issues);
        var before = await new SpecStore(issues).GetAsync(f.Receipt.SpecId);
        Assert.True(await store.CanRepairHygieneAsync(f.Proposal.Id, 1, f.Proposal.Hash, f.Request));
        Assert.True(await store.RepairHygieneAsync(f.Proposal.Id, 1, f.Proposal.Hash, f.Request));
        Assert.False(await store.RepairHygieneAsync(f.Proposal.Id, 1, f.Proposal.Hash, f.Request));
        var after = await new SpecStore(issues).GetAsync(f.Receipt.SpecId);
        Assert.Equal(SpecStatus.Draft, after!.Status);
        Assert.Equal(before!.Body, after.Body); Assert.Equal(before.CurrentVersion, after.CurrentVersion);
        Assert.Equal("Pending", (await store.GetAsync(f.Proposal.Id, 1))!.RefinementState);
        Assert.Contains("epic-1", await store.GetRepairContextAsync(after.Id, after.CurrentVersion));
        Assert.Null(await store.GetRepairContextAsync(after.Id, after.CurrentVersion + 1));
        Assert.Equal(f.Proposal, await new InitiativeProposalStore(issues).GetAsync("test", f.Proposal.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DesignerRunStore(issues.Db).StartAsync(after.Id, DesignerTriggerKind.Scheduled));
    }

    [Fact]
    public async Task RepairQueueReceivesDurableDiagnosticsAfterClaimClearsLastError()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var store = new InitiativeIngestionStore(issues);
        Assert.True(await store.RepairHygieneAsync(f.Proposal.Id, 1, f.Proposal.Hash, f.Request));
        var bus = new Forge.Dashboard.InMemoryDashboardEventBus();
        var service = new Forge.Agents.InitiativeApprovalService(new InitiativeProposalStore(issues), _ => store, bus);
        string? received = null;
        await using var queue = new Forge.Agents.ProductRefinementQueue(service, () => ["test"], _ => store,
            _ => new SpecStore(issues), (_, _, _) => throw new InvalidOperationException("Blind refinement must not run"),
            bus, Microsoft.Extensions.Logging.Abstractions.NullLogger<Forge.Agents.ProductRefinementQueue>.Instance,
            repair: async (_, spec, diagnostics, ct) =>
            {
                received = diagnostics;
                Assert.Null((await store.GetAsync(f.Proposal.Id, 1, ct))!.LastError);
                return await new SpecStore(issues).UpdateBodyAsync(spec, new UpdateSpecBody("corrected approved scope", "product:test"), ct);
            });
        await queue.ReconcileAsync(default);
        Assert.Contains("epic-1", received);
        Assert.Equal("Completed", (await store.GetAsync(f.Proposal.Id, 1))!.RefinementState);
        Assert.Null(await store.GetRepairContextAsync(f.Receipt.SpecId, 2));
    }

    [Fact]
    public async Task FailedResetRollsBackDiagnosticInsertAndSpecChange()
    {
        var f = await Seed(); await using var issues = f.Issues;
        await using (var c = await issues.Db.OpenAsync())
        {
            using var command = c.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_repair BEFORE UPDATE ON initiative_ingestion WHEN NEW.refinement_state='Pending' BEGIN SELECT RAISE(ABORT,'fixture failure'); END";
            await command.ExecuteNonQueryAsync();
        }
        var store = new InitiativeIngestionStore(issues);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.RepairHygieneAsync(f.Proposal.Id, 1, f.Proposal.Hash, f.Request));
        Assert.Equal(SpecStatus.ReadyForDesign, (await new SpecStore(issues).GetAsync(f.Receipt.SpecId))!.Status);
        Assert.Equal("Completed", (await store.GetAsync(f.Proposal.Id, 1))!.RefinementState);
        Assert.Null(await store.GetRepairContextAsync(f.Receipt.SpecId, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiOffersOnlyCoLocatedExactRepairAndRejectsReplay(bool foreignDesignStore)
    {
        var f = await Seed(); await using var issues = f.Issues;
        await using var other = new IssueStore(TempRoot.Instance.NewDbPath("foreign-repair"), "other");
        // Real composition uses distinct wrappers for the same project database.
        await using var primary = new IssueStore(issues.Db, "test");
        var store = new InitiativeIngestionStore(issues);
        var bus = new Forge.Dashboard.InMemoryDashboardEventBus();
        var service = new Forge.Agents.InitiativeApprovalService(new InitiativeProposalStore(issues), _ => store, bus);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        Forge.Dashboard.SpecEndpoints.MapSpecEndpoints(app, new SpecStore(issues), new NullSpecExtractionReader(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, issues: primary, initiativeApprovals: service,
            designerRuns: new DesignerRunStore(foreignDesignStore ? other.Db : issues.Db));
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{((IHost)app).GetPort()}") };
        var action = await http.GetFromJsonAsync<JsonElement>($"/api/specs/{f.Receipt.SpecId}/actions");
        Assert.Equal(!foreignDesignStore, action.GetProperty("canRepairHygiene").GetBoolean());
        var body = new Forge.Dashboard.SpecHygieneRepairRequest(f.Proposal.Id, 1, f.Proposal.Hash, 1, f.Request.DesignRunId);
        using var response = await http.PostAsJsonAsync($"/api/specs/{f.Receipt.SpecId}/refine-again", body);
        Assert.Equal(foreignDesignStore ? HttpStatusCode.Conflict : HttpStatusCode.Accepted, response.StatusCode);
        using var replay = await http.PostAsJsonAsync($"/api/specs/{f.Receipt.SpecId}/refine-again", body);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("run")]
    [InlineData("child")]
    [InlineData("started")]
    public async Task StaleOrDownstreamEvidenceRejectsWithoutPartialWrites(string change)
    {
        var f = await Seed(); await using var issues = f.Issues;
        var store = new InitiativeIngestionStore(issues);
        var request = f.Request;
        if (change == "version") request = request with { SpecVersion = 2 };
        if (change == "run") request = request with { DesignRunId = request.DesignRunId + 1 };
        if (change == "child") await issues.CreateAsync(new NewIssue("story", "existing decomposition", ParentId: f.Receipt.SpecId));
        if (change == "started") await new DesignerRunStore(issues.Db).StartAsync(f.Receipt.SpecId, DesignerTriggerKind.Scheduled);
        Assert.False(await store.RepairHygieneAsync(f.Proposal.Id, 1, change == "hash" ? "wrong" : f.Proposal.Hash, request));
        Assert.Equal(SpecStatus.ReadyForDesign, (await new SpecStore(issues).GetAsync(f.Receipt.SpecId))!.Status);
        Assert.Equal("Completed", (await store.GetAsync(f.Proposal.Id, 1))!.RefinementState);
        Assert.Null(await store.GetRepairContextAsync(f.Receipt.SpecId, 1));
    }

    [Fact]
    public async Task ConcurrentDesignerStartAndRepairCannotBothWin()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var store = new InitiativeIngestionStore(issues);
        var start = Task.Run(async () =>
        {
            try { await new DesignerRunStore(issues.Db).StartAsync(f.Receipt.SpecId, DesignerTriggerKind.Scheduled); return true; }
            catch (InvalidOperationException) { return false; }
        });
        var repair = Task.Run(() => store.RepairHygieneAsync(f.Proposal.Id, 1, f.Proposal.Hash, f.Request));
        Assert.NotEqual(await start, await repair);
    }
}

