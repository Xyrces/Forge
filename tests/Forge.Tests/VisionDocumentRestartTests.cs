using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Projects;
using Xunit;

namespace Forge.Tests;

public sealed class VisionDocumentRestartTests
{
    [Fact]
    public async Task FreshStartupRefreshRestoresSavedVisionInsteadOfOlderRepository()
    {
        var root = TempRoot.Instance.NewDirectory("vision-restart");
        var path = Path.Combine(root, "vision.md");
        const string imported = "# Vision\r\n\r\nOriginal repository guidance.\r\n";
        const string edited = "# Vision\r\n\r\nApproved operator guidance.\r\n";
        await File.WriteAllTextAsync(path, imported);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var options = new[] { new ProjectOptions { Id = "project", Root = root } };
        var databases = new Dictionary<string, string> { ["project"] = Path.Combine(root, "project.db") };
        await using var contexts = new ProjectContextFactory(options, databases);
        var owner = new MemoryStore(((IssueStore)contexts.Find("project")!.Issues).Db);
        var documents = new ProjectVisionDocuments(new VisionStore(root, "vision.md"), contexts, owner);
        var file = documents.Find("project")!;
        await documents.SaveAsync(file, VisionMap.Parse(imported).Revision, _ => edited, default);

        // Simulate interrupted projection writes and recreate all service/store instances.
        await owner.RememberAsync("vision/project", imported);
        await owner.RememberAsync("vision/master", imported);
        await using var restartedContexts = new ProjectContextFactory(options, databases);
        var restartedMemory = new MemoryStore(((IssueStore)restartedContexts.Find("project")!.Issues).Db);
        var restarted = new ProjectVisionDocuments(new VisionStore(root, "vision.md"), restartedContexts, restartedMemory);
        // ForgeComposition invokes this same path for primary and secondary startup.
        var restored = await restarted.RefreshAsync(restarted.Find("project")!, default);

        Assert.Equal(edited, restored.Content);
        Assert.Equal(edited, (await restartedMemory.GetAsync(VisionMemory.DocumentKey("project")))!.Body);
        Assert.Equal(edited, (await restartedMemory.GetAsync("vision/project"))!.Body);
        Assert.Equal(edited, (await restartedMemory.GetAsync("vision/master"))!.Body);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("vision/document/project")]
    [InlineData("vision/project")]
    [InlineData("vision/master")]
    [InlineData(" VISION/master")]
    [InlineData("vísion/master")]
    [InlineData("ｖｉｓｉｏｎ/master")]
    public void GenericMemoryControlsProtectVisionAuthority(string key)
        => Assert.True(AuthorityMemoryKeys.IsProtected(key));
}
