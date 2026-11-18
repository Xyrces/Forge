using System.Diagnostics;
using System.Text.Json;
using Forge.AgentTools;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class ArtifactFilePathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerCanOpenReturnedHistoricalPathFromItsTaskWorktree(bool absoluteStoredPath)
    {
        var root = TempRoot.Instance.NewDirectory("historical-art-file");
        await using var owner = new IssueStore(Path.Combine(root, "owner.db"), "second");
        await using var writer = new IssueStore(Path.Combine(root, "writer.db"), "writer");
        var specs = new SpecStore(owner);
        var spec = await specs.CreateAsync(new("stale-project", "Owned", "body"));
        var outputRoot = Path.Combine(root, "writer-art-output");
        var folder = Path.Combine(outputRoot, spec.Id);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "mesh with space.glb");
        byte[] expected = [0x67, 0x6c, 0x54, 0x46, 0, 17, 255, 42];
        await File.WriteAllBytesAsync(file, expected);
        var stored = absoluteStoredPath ? file : Path.Combine(spec.Id, "mesh with space.glb");
        var artifact = await new ArtOutputStore(writer.Db).CreateAsync(new(spec.Id,
            ArtOutputKind.Mesh, "Historical file", stored, "glb"));
        var history = new ArtifactHistory(specs, _ => Task.FromResult<IReadOnlyList<ArtifactStoreSource>>(
            [new("writer", writer.Db, outputRoot)]));
        var tool = new ArtifactReadTool(new(owner.Db), specs, new(owner.Db),
            new ContextHandoffStore(owner.Db), requireOwnedSpec: true, history: history,
            artOutputRoot: Path.Combine(root, "owner-art-output"));
        using var result = JsonDocument.Parse(await tool.ReadArtifactAsync(artifact.Id));
        var returned = result.RootElement.GetProperty("body").GetString()!;
        Assert.Equal(Path.GetFullPath(file), returned);
        var worktree = Path.Combine(root, "secondary-worktree");
        Directory.CreateDirectory(worktree);
        if (OperatingSystem.IsLinux())
        {
            // Use a real shell from the worker's different working directory. The
            // positional argument keeps filenames out of shell command parsing.
            var start = new ProcessStartInfo("bash")
            {
                WorkingDirectory = worktree, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var arg in new[] { "-c", "cat -- \"$1\"", "artifact-read", returned })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            using var bytes = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(bytes);
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await copy;
            Assert.True(process.ExitCode == 0, await error);
            Assert.Equal(expected, bytes.ToArray());
        }
        else Assert.Equal(expected, await File.ReadAllBytesAsync(returned));
        Assert.Single(await new ContextHandoffStore(owner.Db).ListForTaskAsync(""));
    }

    [Theory]
    [InlineData("../outside.glb")]
    [InlineData("spec-owned/../spec-foreign/mesh.glb")]
    [InlineData("spec-foreign/mesh.glb")]
    [InlineData("spec-owned/../../outside.glb")]
    [InlineData("spec-owned\\..\\spec-foreign\\mesh.glb")]
    [InlineData("https://example.invalid/mesh.glb")]
    [InlineData("spec-owned/missing.glb")]
    public void InvalidMissingAndForeignPathsAreRejected(string body)
    {
        var root = TempRoot.Instance.NewDirectory("invalid-art-file");
        Directory.CreateDirectory(Path.Combine(root, "spec-foreign"));
        File.WriteAllText(Path.Combine(root, "spec-foreign", "mesh.glb"), "foreign");
        Assert.Null(ArtifactFilePath.Resolve(root, "spec-owned", body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedFileOrDirectoryCannotExposeAnotherSpec(bool directoryLink)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TempRoot.Instance.NewDirectory("linked-art-file");
        var owned = Path.Combine(root, "spec-owned");
        var foreign = Path.Combine(root, "spec-foreign");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "mesh.glb"), "foreign");
        if (directoryLink)
            Directory.CreateSymbolicLink(Path.Combine(owned, "linked"), foreign);
        else File.CreateSymbolicLink(Path.Combine(owned, "linked.glb"), Path.Combine(foreign, "mesh.glb"));
        Assert.Null(ArtifactFilePath.Resolve(root, "spec-owned",
            directoryLink ? "spec-owned/linked/mesh.glb" : "spec-owned/linked.glb"));
    }

    [Fact]
    public void TrustedStorageSymlinkResolvesToTheActualFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TempRoot.Instance.NewDirectory("linked-art-root");
        var actual = Path.Combine(root, "actual");
        var linked = Path.Combine(root, "linked");
        Directory.CreateDirectory(Path.Combine(actual, "spec-owned"));
        Directory.CreateSymbolicLink(linked, actual);
        var file = Path.Combine(actual, "spec-owned", "mesh.glb");
        File.WriteAllText(file, "owned bytes");
        Assert.Equal(file, ArtifactFilePath.Resolve(linked, "spec-owned", "spec-owned/mesh.glb"));
    }

    [Fact]
    public async Task NamedPipeIsRejectedBeforeOpening()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TempRoot.Instance.NewDirectory("fifo-art-file");
        Directory.CreateDirectory(Path.Combine(root, "spec-owned"));
        var fifo = Path.Combine(root, "spec-owned", "mesh.glb");
        var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
        start.ArgumentList.Add(fifo);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Null(ArtifactFilePath.Resolve(root, "spec-owned", "spec-owned/mesh.glb"));
    }
}
