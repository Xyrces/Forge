using System.Text.Json;
using Forge.Core;
using Forge.Orchestrator;
using Forge.Orchestrator.Sprint;
using Xunit;

namespace Forge.Tests;

public sealed class ImplementationFormatWriteTests
{
    internal static async Task Stamp41(IssueStore issues)
    {
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM schema_version WHERE version>=42; INSERT OR IGNORE INTO schema_version(version,applied_at) VALUES(41,'fixture')";
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("object")]
    [InlineData("encoded")]
    [InlineData("numeric")]
    [InlineData("numeric-string")]
    [InlineData("proof")]
    public async Task AllMetadataEntryPointsReject42On41WithoutChangingTask(string shape)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("format-writes"), "project");
        var task = await issues.CreateAsync(new("task", "Legacy task"));
        task = (await issues.GetAsync(task.Id))!;
        await Stamp41(issues);
        var binding = "{\"criteria\":[{\"evidenceKind\":\"ImplementationVerified\"}]}";
        Dictionary<string, object> additions = shape switch
        {
            "encoded" => new() { [SprintPlanJson.MetadataKey] = binding },
            "numeric" => new() { [SprintPlanJson.MetadataKey] = JsonSerializer.Deserialize<JsonElement>("{\"criteria\":[{\"evidenceKind\":3}]}") },
            "numeric-string" => new() { [SprintPlanJson.MetadataKey] = JsonSerializer.Deserialize<JsonElement>("{\"criteria\":[{\"evidenceKind\":\"3\"}]}") },
            "proof" => new() { ["trustedExistingImplementation"] = "untrusted" },
            _ => new() { [SprintPlanJson.MetadataKey] = JsonSerializer.Deserialize<JsonElement>(binding) }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.CreateAsync(new("task", "Forbidden", Metadata: additions)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.MergePendingMetadataAsync(task.Id, additions));
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.TransitionAsync(task.Id, IssueStatus.Completed, null, additions));
        Assert.Equal(task, await issues.GetAsync(task.Id));
        Assert.Single(await issues.ListAsync(new IssueFilter()));
        // The compatibility bridge must remain useful for legacy work on 41.
        await issues.TransitionAsync(task.Id, IssueStatus.InProgress, null, new Dictionary<string, object> { ["legacyNote"] = "unchanged contract" });
        Assert.Equal(IssueStatus.InProgress, (await issues.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task UnreadableHistoricalBindingDoesNotPreventExplicitParking()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("format-legacy-malformed"), "project");
        var task = await issues.CreateAsync(new("task", "Legacy task", Metadata: new Dictionary<string, object>
        { [SprintPlanJson.MetadataKey] = "{broken historical JSON" }));
        await Stamp41(issues);
        await issues.TransitionAsync(task.Id, IssueStatus.Blocked, "Investigate malformed mapping");
        var parked = (await issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Blocked, parked.Status);
        Assert.Equal("{broken historical JSON", parked.GetMetadata(SprintPlanJson.MetadataKey));
        Assert.Null(SprintPlanJson.ReadBinding(parked));
    }

    [Fact]
    public async Task ProofMetadataCannotBypassCanonicalAdmissionEvenOn42()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("format-reserved"), "project");
        var task = await issues.CreateAsync(new("task", "Task"));
        var proof = new Dictionary<string, object> { ["trustedExistingImplementation"] = "untrusted" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.CreateAsync(new("task", "Forged", Metadata: proof)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.MergePendingMetadataAsync(task.Id, proof));
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.TransitionAsync(task.Id, IssueStatus.Completed, null, proof));
        Assert.Equal(IssueStatus.Pending, (await issues.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task DraftAndCommitPathsRejectNewKindBeforeAnySprintMutation()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("format-plan"), "project");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var task = await ApprovedSprintFixture.Task(issues);
        var criterion = new SprintCriterion("behavior", "Approved behavior", SprintEvidenceKind.ImplementationVerified, scope);
        await new SprintOutcomeAuthority(issues).BindAsync(task.Id,
            new(1, "unit", "Outcome", [criterion], [task.Id], [criterion.Id], SprintMemberReason.Delivers, scope));
        var sprints = new SprintStore(issues);
        var snapshot = await new SprintActivationValidator(issues, sprints, new SpecStore(issues), issues.ProjectId, null).ValidateAsync([task.Id], default);
        await using var audits = new SprintProposalAuditStore(issues.Db);
        var weights = new Dictionary<string, object> { ["plan"] = JsonSerializer.SerializeToElement(snapshot.Plan, SprintPlanJson.Options) };
        var prior = await audits.RecordAsync("Outcome", "Outcome", weights, [], [task.Id]);
        await Stamp41(issues);
        await Assert.ThrowsAsync<InvalidOperationException>(() => audits.RecordAsync("Outcome", "Outcome", weights, [], [task.Id]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => audits.MarkCommittedAsync(prior, "fake", "test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.CreateWithIssuesAsync(new("Outcome", "Outcome", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot));
        Assert.Null(await sprints.GetActiveAsync());
        Assert.Single(await audits.ListAsync());
        Assert.Null((await audits.ListAsync())[0].CommittedSprintId);
    }
}
