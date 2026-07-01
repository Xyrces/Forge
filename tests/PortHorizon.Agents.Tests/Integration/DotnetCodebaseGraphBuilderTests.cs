using System.Diagnostics;
using System.Text.Json;
using PortHorizon.Agents.Codebase;
using Xunit;

namespace PortHorizon.Agents.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="DotnetCodebaseGraphBuilder"/>.
/// Each test creates a small fixture repo on disk in a temp dir,
/// optionally runs <c>git init</c> + <c>git commit</c> so the
/// incremental-cache paths can be exercised, then builds the
/// graph and asserts on its contents.
/// </summary>
public class DotnetCodebaseGraphBuilderTests : IDisposable
{
    private readonly string _repoRoot;

    public DotnetCodebaseGraphBuilderTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"ph-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
    }

    private static void Run(string dir, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {args}",
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        p!.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command '{args}' failed in {dir}: {p.StandardError.ReadToEnd()}");
    }

    private void InitGit()
    {
        // Use cmd.exe to invoke git; --initial-branch avoids the
        // git-config warning that pollutes stderr.
        Run(_repoRoot, "git init -q --initial-branch=main");
        Run(_repoRoot, "git config user.email test@example.com");
        Run(_repoRoot, "git config user.name Test");
    }

    private void Commit()
    {
        Run(_repoRoot, "git add -A");
        Run(_repoRoot, "git commit -q -m test");
    }

private void WriteCsproj(string module, params string[] projectRefs)
    {
        string content;
        if (projectRefs.Length == 0)
        {
            content = "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n";
        }
        else
        {
            var refs = string.Join("\n    ", projectRefs.Select(p => "<ProjectReference Include=\"" + p + "\" />"));
            content = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    " + refs + "\n  </ItemGroup>\n</Project>\n";
        }
        File.WriteAllText(Path.Combine(_repoRoot, module + ".csproj"), content);
    }

    private void WriteCs(string relPath, params string[] usings)
    {
        var dir = Path.GetDirectoryName(relPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(Path.Combine(_repoRoot, dir));
        var usingLines = string.Join("\n", usings.Select(u => "using " + u + ";"));
        var content = "namespace X;\n\n" + usingLines + "\n\npublic class A { }\n";
        File.WriteAllText(Path.Combine(_repoRoot, relPath), content);
    }

    [Fact]
    public async Task ColdWalk_ParsesCsprojProjectReferences()
    {
        WriteCsproj("A");
        WriteCsproj("B", "../A/A.csproj");
        WriteCs("A/Program.cs");
        WriteCs("B/Program.cs");

        var builder = new DotnetCodebaseGraphBuilder();
        var graph = await builder.BuildAsync(_repoRoot, priorCache: null, cacheDirectory: null);

        // 1 project edge: B references A. (A has no outgoing reference.)
        Assert.Single(graph.Projects);
        Assert.Single(graph.Projects, p => p.FromProject == "B" && p.ToProject == "A");
    }

    [Fact]
    public async Task ColdWalk_ParsesUsingDirectives()
    {
        WriteCsproj("A");
        WriteCs("A/A.cs", "System", "System.Collections.Generic", "Foo.Bar.Baz");

        var builder = new DotnetCodebaseGraphBuilder();
        var graph = await builder.BuildAsync(_repoRoot, priorCache: null, cacheDirectory: null);

        // System.* usings are filtered (BCL); Foo.Bar.Baz should be there.
        Assert.Single(graph.Imports);
        Assert.Equal("A/A.cs", graph.Imports[0].From);
        Assert.Equal("Foo.Bar.Baz", graph.Imports[0].To);
    }

    [Fact]
    public async Task ColdWalk_PersistsJsonCache()
    {
        WriteCsproj("A");
        WriteCs("A/A.cs");

        var cacheDir = Path.Combine(_repoRoot, ".portHorizon", "codebase-graph");
        var builder = new DotnetCodebaseGraphBuilder();
        await builder.BuildAsync(_repoRoot, priorCache: null, cacheDirectory: cacheDir);

        var files = Directory.GetFiles(cacheDir, "*.json");
        Assert.Single(files);
        // Round-trip.
        var json = await File.ReadAllTextAsync(files[0]);
        var roundTripped = JsonSerializer.Deserialize<CodebaseGraph>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(_repoRoot, roundTripped!.RepoRoot);
    }

    [Fact]
    public async Task WarmSameSha_ReturnsPriorGraphWithoutReparsing()
    {
        InitGit();
        WriteCsproj("A");
        WriteCs("A/A.cs", "Foo.Bar");
        Commit();

        var cacheDir = Path.Combine(_repoRoot, ".portHorizon", "codebase-graph");
        var builder = new DotnetCodebaseGraphBuilder();
        var first = await builder.BuildAsync(_repoRoot, priorCache: null, cacheDirectory: cacheDir);

        // Find the cache file + sha.
        var cacheFile = Directory.GetFiles(cacheDir, "*.json").Single();
        var sha = Path.GetFileNameWithoutExtension(cacheFile);

        // Warm call with the same sha: no changes to repo, no sha bump,
        // no re-parse. Returned graph should equal the first.
        var prior = new CodebaseGraphCache(
            BuiltAt: first.BuiltAt,
            RepoSha: sha,
            FileCount: first.Files.Count,
            EdgeCount: first.Imports.Count + first.Projects.Count,
            DiskPath: cacheFile);

        var second = await builder.BuildAsync(_repoRoot, priorCache: prior, cacheDirectory: cacheDir);
        Assert.Equal(first.Files.Count, second.Files.Count);
        Assert.Equal(first.Imports.Count, second.Imports.Count);
    }

    [Fact]
    public async Task WarmChangedSha_IncrementalReparsesOnlyChangedFiles()
    {
        InitGit();
        WriteCsproj("A");
        WriteCs("A/A.cs", "Foo.Bar");
        WriteCs("A/B.cs", "Foo.Bar");
        Commit();

        var cacheDir = Path.Combine(_repoRoot, ".portHorizon", "codebase-graph");
        var builder = new DotnetCodebaseGraphBuilder();
        var first = await builder.BuildAsync(_repoRoot, priorCache: null, cacheDirectory: cacheDir);
        Assert.Equal(2, first.Files.Count);

        var cacheFile = Directory.GetFiles(cacheDir, "*.json").Single();
        var sha = Path.GetFileNameWithoutExtension(cacheFile);

        // Change only one file.
        WriteCs("A/B.cs", "Foo.Bar", "NewDep.X");
        Commit();

        var prior = new CodebaseGraphCache(
            BuiltAt: first.BuiltAt,
            RepoSha: sha,
            FileCount: first.Files.Count,
            EdgeCount: first.Imports.Count + first.Projects.Count,
            DiskPath: cacheFile);

        var second = await builder.BuildAsync(_repoRoot, priorCache: prior, cacheDirectory: cacheDir);
        // Still 2 files (no deletion, no addition).
        Assert.Equal(2, second.Files.Count);
        // A.cs's old edges remain.
        Assert.Contains(second.Imports, e => e.From == "A/A.cs");
        // B.cs's edges were re-parsed and now include NewDep.X.
        Assert.Contains(second.Imports, e => e.From == "A/B.cs" && e.To == "NewDep.X");
    }

    [Fact]
    public void ResolveRelativePath_HandlesDotDot()
    {
        // Direct unit test of the path normalizer.
        Assert.Equal("src/A/A.csproj", DotnetCodebaseGraphBuilder.ResolveRelativePath("src/B", "../A/A.csproj"));
        Assert.Equal("A/A.csproj", DotnetCodebaseGraphBuilder.ResolveRelativePath("", "A/A.csproj"));
        // "./Foo" from base "src/Bar" means "Foo inside src/Bar", i.e.
        // "src/Bar/Foo" — . is the current directory, not a no-op.
        Assert.Equal("src/Bar/Foo", DotnetCodebaseGraphBuilder.ResolveRelativePath("src/Bar", "./Foo"));
        // "../A" from base "src/B" pops "B" and pushes "A": src/A.
        Assert.Equal("src/A", DotnetCodebaseGraphBuilder.ResolveRelativePath("src/B", "../A"));
    }

    [Fact]
    public void SupportsLanguage_RecognizesCsharp()
    {
        var builder = new DotnetCodebaseGraphBuilder();
        Assert.True(builder.SupportsLanguage("csharp"));
        Assert.True(builder.SupportsLanguage("csproj"));
        Assert.False(builder.SupportsLanguage("typescript"));
    }
}