using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class ProjectWorkerEnvironmentTests : IDisposable
{
    private readonly IssueStore _issues = new(TempRoot.Instance.NewDbPath("worker-environment"));
    private ProjectStore Projects => new(_issues);
    private ProjectWorkerEnvironmentStore Environments => new(Projects);
    private static string Image(char digest = 'a') => "registry.example.invalid/worker@sha256:" + new string(digest, 64);
    public void Dispose() => _issues.Dispose();

    private async Task<ProjectWorkerEnvironment> Define(string id = "first")
    {
        await Projects.UpsertAsync(new(id, id, "https://example.invalid/repo"));
        return (await Environments.SetDefinitionAsync(id, new("0", "deploy/Workerfile")))!;
    }

    [Fact]
    public async Task DefaultAndUnknownProjectsUseStockWithoutInventingRegistryRows()
    {
        await Projects.UpsertAsync(new("first", "First", "https://example.invalid/repo"));
        Assert.Equal("stock", (await Environments.GetAsync("first"))!.Status);
        Assert.Null(await Environments.ResolveImageAsync("first"));
        Assert.Null(await Environments.SetDefinitionAsync("missing", new("0", "Dockerfile")));
        Assert.Null(await Environments.GetAsync("missing"));
    }

    [Fact]
    public async Task ApprovalPersistsInOwningRegistryAndPendingDefinitionRetainsLastWorkingImage()
    {
        var definition = await Define();
        await Define("second");
        var active = (await Environments.QualifyAsync("first", new(definition.Revision, definition.DefinitionRevision, Image(), new string('b', 40))))!;
        Assert.Equal("active", (await new ProjectWorkerEnvironmentStore(new ProjectStore(_issues)).GetAsync("first"))!.Status);
        Assert.Null(await Environments.ResolveImageAsync("second"));
        var pending = (await Environments.SetDefinitionAsync("first", new(active.Revision, "tools/Dockerfile", "tools")))!;
        Assert.Equal("pending", pending.Status);
        Assert.Equal(Image(), await Environments.ResolveImageAsync("first"));
        Assert.Equal("deploy/Workerfile", pending.ApprovedDockerfilePath);
        Assert.Equal(active.DefinitionRevision, pending.ApprovedDefinitionRevision);
        Assert.Equal(pending, await Environments.SetDefinitionAsync("first", new(pending.Revision, "tools/Dockerfile", "tools")));
        var cleared = (await Environments.SetDefinitionAsync("first", new(pending.Revision, null)))!;
        Assert.Equal("stock", cleared.Status);
        Assert.Null(cleared.ApprovedImage);
        Assert.Null(await Environments.ResolveImageAsync("first"));
    }

    [Fact]
    public async Task StaleBuildersAndEditorsCannotReplaceNewerApprovalOrDefinition()
    {
        var definition = await Define();
        var request = new QualifyWorkerEnvironment(definition.Revision, definition.DefinitionRevision, Image(), new string('b', 40));
        var first = (await Environments.QualifyAsync("first", request))!;
        await Assert.ThrowsAsync<ProjectSettingsConflictException>(() => Environments.QualifyAsync("first", request with { Image = Image('c') }));
        await Assert.ThrowsAsync<ProjectSettingsConflictException>(() => Environments.SetDefinitionAsync("first", new(definition.Revision, "other/Dockerfile")));
        await Assert.ThrowsAsync<ProjectSettingsConflictException>(() => Environments.QualifyAsync("first", request with { ExpectedRevision = first.Revision, DefinitionRevision = "stale" }));
        Assert.Equal(first, await Environments.GetAsync("first"));
    }

    [Fact]
    public async Task ConcurrentSiblingSettingsDoNotEraseEnvironmentApproval()
    {
        var definition = await Define();
        await Task.WhenAll(
            Environments.QualifyAsync("first", new(definition.Revision, definition.DefinitionRevision, Image(), new string('b', 40))),
            Projects.UpdateRolesAsync("first", new Dictionary<string, int> { ["coredev"] = 4, ["$workerEnvironment"] = 5 }),
            Projects.UpdateTriageAsync("first", true),
            Projects.UpdateVerifyCommandsAsync("first", ["make test"]),
            Projects.UpdateTerritoriesAsync("first", new Dictionary<string, RoleTerritory> { ["coredev"] = new(["src/"], false) }),
            Projects.UpdateQaAsync("first", true, ["ui/"]));
        var project = (await Projects.GetAsync("first"))!;
        Assert.Equal(4, project.Roles["coredev"]);
        Assert.True(project.TriageEnabled);
        Assert.True(project.QaEnabled);
        Assert.Equal(new[] { "make test" }, project.VerifyCommands);
        Assert.Equal(new[] { "ui/" }, project.QaVisualPaths);
        Assert.Equal(new[] { "src/" }, project.Territories["coredev"].Prefixes);
        Assert.Equal(Image(), await Environments.ResolveImageAsync("first"));
        await Projects.UpsertAsync(new("first", "Renamed", "https://example.invalid/repo"));
        Assert.Equal(Image(), await Environments.ResolveImageAsync("first"));
    }

    [Theory]
    [InlineData("https://example.invalid/other", "main")]
    [InlineData("https://example.invalid/repo", "release")]
    public async Task SourceRebindWithdrawsOldApprovalAndRejectsInflightBuild(string repository, string branch)
    {
        var definition = await Define();
        var active = (await Environments.QualifyAsync("first", new(definition.Revision, definition.DefinitionRevision, Image(), new string('b', 40))))!;
        await Projects.UpdateRolesAsync("first", new Dictionary<string, int> { ["coredev"] = 3 });
        await Projects.UpsertAsync(new("first", "First", repository, branch));
        var rebound = (await Environments.GetAsync("first"))!;
        Assert.Equal("pending", rebound.Status);
        Assert.Equal(active.DockerfilePath, rebound.DockerfilePath);
        Assert.NotEqual(active.DefinitionRevision, rebound.DefinitionRevision);
        Assert.Null(await Environments.ResolveImageAsync("first"));
        Assert.Null(rebound.ApprovedSourceRevision);
        Assert.Equal(3, (await Projects.GetAsync("first"))!.Roles["coredev"]);
        await Assert.ThrowsAsync<ProjectSettingsConflictException>(() => Environments.QualifyAsync("first",
            new(active.Revision, active.DefinitionRevision, Image('c'), new string('b', 40))));
    }

    [Fact]
    public async Task DeletingAndReregisteringSameIdCannotAcceptOldBuild()
    {
        var old = await Define();
        Assert.True(await Projects.DeleteAsync("first"));
        var replacement = await Define();
        Assert.NotEqual(old.DefinitionRevision, replacement.DefinitionRevision);
        await Assert.ThrowsAsync<ProjectSettingsConflictException>(() => Environments.QualifyAsync("first",
            new(old.Revision, old.DefinitionRevision, Image(), new string('b', 40))));
        Assert.Null(await Environments.ResolveImageAsync("first"));
    }

    [Fact]
    public async Task SimultaneousQualificationsHaveExactlyOneWinner()
    {
        var definition = await Define();
        async Task<bool> Qualify(char digest)
        {
            try
            {
                await Environments.QualifyAsync("first", new(definition.Revision, definition.DefinitionRevision, Image(digest), new string('b', 40)));
                return true;
            }
            catch (ProjectSettingsConflictException) { return false; }
        }
        var results = await Task.WhenAll(Qualify('a'), Qualify('c'));
        Assert.Single(results, succeeded => succeeded);
    }

    [Fact]
    public async Task DockerfileCannotEscapeItsSelectedBuildContext()
        => await Assert.ThrowsAsync<ArgumentException>(() => Environments.SetDefinitionAsync("first", new("0", "Dockerfile", "tools")));

    [Theory]
    [InlineData("../Dockerfile")]
    [InlineData("/Dockerfile")]
    [InlineData("C:/Dockerfile")]
    [InlineData("tools\\Dockerfile")]
    [InlineData("tools//Dockerfile")]
    [InlineData("")]
    public async Task DefinitionRejectsPathsOutsideTheTrackedRepository(string path)
        => await Assert.ThrowsAsync<ArgumentException>(() => Environments.SetDefinitionAsync("first", new("0", path)));

    [Theory]
    [InlineData("worker:latest")]
    [InlineData("https://example.invalid/image")]
    [InlineData("worker@sha256:bad")]
    public async Task QualificationRejectsMutableOrMalformedImageReferences(string image)
        => await Assert.ThrowsAsync<ArgumentException>(() => Environments.QualifyAsync("first", new("0", "0", image, new string('a', 40))));
}
