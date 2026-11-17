using System.Text.Json;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Orchestrator;
using Forge.Orchestrator.Sprint;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class OperatorAcceptanceRepairTests
{
    private sealed record Fixture(IssueStore Issues, SprintScopeReference Scope, long RunId, IssueRecord[] Targets, IssueRecord[] Sources);
    private static async Task<Fixture> Seed()
    {
        var issues = new IssueStore(TempRoot.Instance.NewDbPath("operator-acceptance-repair"), "test");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var specs = new SpecStore(issues);
        await specs.SetStatusAsync(scope.SpecId, SpecStatus.Designed);
        await specs.SetStatusAsync(scope.SpecId, SpecStatus.Grooming);
        var run = await new IssueGroomerRunStore(issues.Db).StartAsync(scope.SpecId, GroomerTriggerKind.Scheduled, default);
        var implementation = await issues.CreateAsync(new NewIssue("story", "Implementation", ParentId: scope.SpecId));
        var observations = await issues.CreateAsync(new NewIssue("story", "Operator checks", ParentId: scope.SpecId));
        var targets = new[]
        {
            await issues.CreateAsync(new NewIssue("task", "API", ParentId: implementation.Id)),
            await issues.CreateAsync(new NewIssue("client", "UI", ParentId: implementation.Id))
        };
        var sources = new[]
        {
            await issues.CreateAsync(new NewIssue("qa", "Browser observations", "Operator must observe real browser", ParentId: observations.Id)),
            await issues.CreateAsync(new NewIssue("qa", "Image observations", "Operator must compare deployed artifact", ParentId: observations.Id))
        };
        await ApprovedSprintFixture.Bind(issues, scope, targets);
        var post = new[] { new SprintCriterion("browser", "Actual browser evidence", SprintEvidenceKind.OperatorObserved, scope),
            new SprintCriterion("image", "Actual running-image evidence", SprintEvidenceKind.OperatorObserved, scope) };
        foreach (var source in sources)
        {
            // Persist the legacy malformed decomposition without weakening the new grooming guard.
            var binding = new SprintOutcomeBinding(1, "operator-unit", "Observe deployment", post, sources.Select(x => x.Id).ToArray(),
                [post[Array.IndexOf(sources, source)].Id], SprintMemberReason.Delivers, scope);
            await issues.MergePendingMetadataAsync(source.Id, new Dictionary<string, object> { [SprintPlanJson.MetadataKey] = JsonSerializer.SerializeToElement(binding, SprintPlanJson.Options) });
            foreach (var target in targets) await issues.AddDependencyAsync(target.Id, source.Id, IssueDepKind.Blocks);
        }
        await specs.SetStatusAsync(scope.SpecId, SpecStatus.Groomed);
        await new IssueGroomerRunStore(issues.Db).FinishAsync(run.Id, GroomerRunStatus.Succeeded, 2, 4, null, TimeSpan.Zero, default);
        return new(issues, scope, run.Id, targets, sources);
    }
    private static MoveOperatorChecksRequest Request(OperatorCheckAcceptancePreview p) => new(p.SpecVersion, p.GroomRunId, p.PreviewHash);

    [Fact]
    public async Task RepairPreservesExactAcceptanceAndOnlyImplementationCanProceedToDeployment()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var repair = new OperatorAcceptanceRepair(issues);
        var preview = Assert.IsType<OperatorCheckAcceptancePreview>((await repair.PreviewAsync(f.Scope.SpecId)).Preview);
        Assert.Equal(2, preview.SourceRows.Count); Assert.Equal(2, preview.TargetRows.Count);
        var receipt = await repair.ApplyAsync(f.Scope.SpecId, Request(preview), "operator");
        Assert.Equal(SprintPlanJson.Serialize(receipt), SprintPlanJson.Serialize(await repair.ApplyAsync(f.Scope.SpecId, Request(preview), "operator")));
        foreach (var source in f.Sources)
        {
            var row = (await issues.GetAsync(source.Id))!;
            Assert.Equal(IssueStatus.Closed, row.Status);
            Assert.Equal("Cancelled", row.GetMetadata("closeDisposition"));
            Assert.False(IssueResolution.IsSatisfied(row));
            Assert.Equal(preview.SourceRows.Single(x => x.Id == row.Id).GetMetadata(SprintPlanJson.MetadataKey), row.GetMetadata(SprintPlanJson.MetadataKey));
        }
        var sprints = new SprintStore(issues);
        var snapshot = await new SprintActivationValidator(issues, sprints, new SpecStore(issues), "test", null)
            .ValidateAsync(f.Targets.Select(x => x.Id).ToArray(), default);
        Assert.Equal(3, snapshot.Plan!.Criteria.Count);
        Assert.All(preview.TransferredCriteria, criterion => Assert.Contains(criterion, snapshot.Plan.Criteria));
        Assert.Equal(2, snapshot.TaskIds.Count);
        var sprint = await sprints.CreateWithIssuesAsync(new("Delivery", "Delivery", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot);
        foreach (var target in f.Targets)
        {
            var sha = new string('a', 40);
            await issues.TransitionAsync(target.Id, IssueStatus.Completed, null, new Dictionary<string, object>
            {
                ["prNumber"] = "1", ["branchSha"] = sha,
                ["trustedPublication"] = JsonSerializer.Serialize(new TrustedPublicationEvidence(target.Id, sha,
                    new("attempt", new string('b', 40), new string('c', 64), DateTimeOffset.UtcNow))),
                ["observedMerge"] = JsonSerializer.Serialize(new ObservedMergeEvidence(target.Id, 1, sha, sha, DateTimeOffset.UtcNow))
            });
        }
        var completion = await new CompletionCoordinator(issues, null, null, NullLogger.Instance).CompleteAsync(sprint, sprints, default);
        Assert.True(completion.ReadyForDeployment);
        Assert.Equal(EngineeringOutcome.PendingEvidence, completion.EngineeringOutcome);
        Assert.Equal(SpecStatus.Groomed, (await new SpecStore(issues).GetAsync(f.Scope.SpecId))!.Status);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("run")]
    [InlineData("binding")]
    [InlineData("edge")]
    [InlineData("claimed")]
    [InlineData("pr")]
    [InlineData("history")]
    [InlineData("membership")]
    public async Task ChangedOrUsedRowsRejectWithoutPartialRepair(string change)
    {
        var f = await Seed(); await using var issues = f.Issues;
        var repair = new OperatorAcceptanceRepair(issues);
        var preview = (await repair.PreviewAsync(f.Scope.SpecId)).Preview!;
        var request = Request(preview);
        if (change == "version") request = request with { SpecVersion = 2 };
        if (change == "run") request = request with { GroomRunId = f.RunId + 1 };
        if (change == "binding") await issues.MergePendingMetadataAsync(f.Targets[0].Id, new Dictionary<string, object> { ["changed"] = true });
        if (change == "edge") await issues.RemoveDependencyAsync(f.Targets[0].Id, f.Sources[0].Id, IssueDepKind.Blocks);
        if (change == "claimed") await issues.ClaimAsync(f.Targets[0].Id, "agent");
        if (change == "pr") await issues.MergePendingMetadataAsync(f.Targets[0].Id, new Dictionary<string, object> { ["prNumber"] = "1" });
        if (change == "history") await issues.AddEventAsync(f.Targets[0].Id, "claimed", "past attempt");
        if (change == "membership")
        {
            var sprint = await new SprintStore(issues).CreateAsync(new("Draft", "Draft", DateTime.UtcNow, DateTime.UtcNow, SprintStatus.Archived));
            await new SprintStore(issues).AddIssueAsync(sprint.Id, f.Targets[0].Id);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ApplyAsync(f.Scope.SpecId, request, "operator"));
        Assert.All(await Task.WhenAll(f.Sources.Select(x => issues.GetAsync(x.Id))), row => Assert.Equal(IssueStatus.Pending, row!.Status));
        Assert.Null((await issues.GetAsync(f.Targets[1].Id))!.GetMetadata("operatorAcceptanceRepair"));
    }

    [Theory]
    [InlineData("consumer")]
    [InlineData("mixed")]
    [InlineData("two-units")]
    [InlineData("cycle")]
    [InlineData("changed-spec")]
    [InlineData("uncovered")]
    public async Task AmbiguousOrInvalidMappingHasNoEligiblePreview(string change)
    {
        var f = await Seed(); await using var issues = f.Issues;
        if (change == "consumer")
        {
            var consumer = await issues.CreateAsync(new NewIssue("task", "External consumer"));
            await issues.AddDependencyAsync(f.Sources[0].Id, consumer.Id, IssueDepKind.Blocks);
        }
        if (change is "mixed" or "two-units" or "uncovered")
        {
            var row = (await issues.GetAsync(change == "mixed" ? f.Sources[0].Id : f.Targets[0].Id))!;
            var binding = SprintPlanJson.ReadBinding(row)!;
            binding = change switch
            {
                "mixed" => binding with { Criteria = [.. binding.Criteria, new("mixed", "Engineering work too", SprintEvidenceKind.MergedAndVerified, f.Scope)] },
                "two-units" => binding with { OutcomeId = "different-goal" },
                _ => binding with { Criteria = [.. binding.Criteria, new("uncovered", "No task owns this criterion", SprintEvidenceKind.MergedAndVerified, f.Scope)] }
            };
            await issues.MergePendingMetadataAsync(row.Id, new Dictionary<string, object> { [SprintPlanJson.MetadataKey] = JsonSerializer.SerializeToElement(binding, SprintPlanJson.Options) });
        }
        if (change == "cycle")
        {
            await issues.AddDependencyAsync(f.Targets[0].Id, f.Targets[1].Id, IssueDepKind.Blocks);
            await issues.AddDependencyAsync(f.Targets[1].Id, f.Targets[0].Id, IssueDepKind.Blocks);
        }
        if (change == "changed-spec") await new SpecStore(issues).UpdateBodyAsync(f.Scope.SpecId, new("New body after grooming", "operator"));
        var result = await new OperatorAcceptanceRepair(issues).PreviewAsync(f.Scope.SpecId);
        Assert.False(result.Eligible); Assert.Null(result.Preview); Assert.NotNull(result.Reason);
        Assert.All(await Task.WhenAll(f.Sources.Select(x => issues.GetAsync(x.Id))), row => Assert.Equal(IssueStatus.Pending, row!.Status));
    }

    [Fact]
    public async Task ActivationSnapshotTakenBeforeRepairCannotCommitAfterwards()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var sprints = new SprintStore(issues);
        var snapshot = await new SprintActivationValidator(issues, sprints, new SpecStore(issues), "test", null)
            .ValidateAsync(f.Targets.Select(x => x.Id).ToArray(), default);
        var repair = new OperatorAcceptanceRepair(issues);
        var preview = (await repair.PreviewAsync(f.Scope.SpecId)).Preview!;
        await repair.ApplyAsync(f.Scope.SpecId, Request(preview), "operator");
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.CreateWithIssuesAsync(new("Old plan", "Old plan", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot));
        Assert.Null(await sprints.GetActiveAsync());
    }

    [Fact]
    public async Task ActivationCommittedBeforeRepairPreventsNormalization()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var sprints = new SprintStore(issues);
        var repair = new OperatorAcceptanceRepair(issues);
        var preview = (await repair.PreviewAsync(f.Scope.SpecId)).Preview!;
        var snapshot = await new SprintActivationValidator(issues, sprints, new SpecStore(issues), "test", null)
            .ValidateAsync(f.Targets.Select(x => x.Id).ToArray(), default);
        await sprints.CreateWithIssuesAsync(new("Accepted plan", "Accepted plan", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ApplyAsync(f.Scope.SpecId, Request(preview), "operator"));
        Assert.All(await Task.WhenAll(f.Sources.Select(x => issues.GetAsync(x.Id))), row => Assert.Equal(IssueStatus.Pending, row!.Status));
    }

    [Fact]
    public async Task FailureAfterTargetMutationRollsBackEveryRowAndReceipt()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var repair = new OperatorAcceptanceRepair(issues);
        var preview = (await repair.PreviewAsync(f.Scope.SpecId)).Preview!;
        await using (var connection = await issues.Db.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_repair BEFORE UPDATE ON issue WHEN NEW.status='Closed' BEGIN SELECT RAISE(ABORT,'fixture rollback'); END";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => repair.ApplyAsync(f.Scope.SpecId, Request(preview), "operator"));
        var after = (await repair.PreviewAsync(f.Scope.SpecId)).Preview!;
        Assert.Equal(preview.PreviewHash, after.PreviewHash);
    }

    [Fact]
    public async Task ConcurrentSameRepairReturnsOneDurableReceipt()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var repair = new OperatorAcceptanceRepair(issues);
        var preview = (await repair.PreviewAsync(f.Scope.SpecId)).Preview!;
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => repair.ApplyAsync(f.Scope.SpecId, Request(preview), "operator"))));
        Assert.Equal(results[0].AppliedAt, results[1].AppliedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ApplyAsync(f.Scope.SpecId, Request(preview) with { PreviewHash = "changed" }, "operator"));
    }
}
