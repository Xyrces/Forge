using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class VisionMemoryTests : IDisposable
{
    private readonly string _directory = TempRoot.Instance.NewDirectory("vision-memory");
    private readonly IssueStore _issues;
    private readonly MemoryStore _memory;

    public VisionMemoryTests()
    {
        var database = Path.Combine(_directory, "issues.db");
        _issues = new IssueStore(database);
        _memory = new MemoryStore(database);
    }

    [Fact]
    public async Task ReadsExactProjectKeyAndPreservesAnExplicitlyEmptyVision()
    {
        await _memory.RememberAsync("vision/forge-extra", "Different project");
        await _memory.RememberAsync("vision/forge/history", "Historical source");
        await _memory.RememberAsync("vision/master", "Legacy vision");
        Assert.Null(await VisionMemory.ReadAsync(_memory, "forge"));
        await _memory.RememberAsync("vision/forge", "Exact vision");
        Assert.Equal("Exact vision", (await VisionMemory.ReadAsync(_memory, "forge", "forge"))!.Body);
        await _memory.RememberAsync("vision/forge", "");
        Assert.Equal("", (await VisionMemory.ReadAsync(_memory, "forge", "forge"))!.Body);
    }

    [Fact]
    public async Task LegacyAliasIsAvailableOnlyToItsOwnerOrAnUnattributedLegacyCaller()
    {
        await _memory.RememberAsync("vision/master", "Primary project vision");
        await _memory.RememberAsync("vision/master-extra", "Not the legacy alias");
        Assert.Equal("Primary project vision", (await VisionMemory.ReadAsync(_memory, null))!.Body);
        Assert.Equal("Primary project vision", (await VisionMemory.ReadAsync(_memory, "forge", "forge"))!.Body);
        Assert.Null(await VisionMemory.ReadAsync(_memory, "second", "forge"));
        Assert.Null(await VisionMemory.ReadAsync(_memory, "second"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        _issues.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
