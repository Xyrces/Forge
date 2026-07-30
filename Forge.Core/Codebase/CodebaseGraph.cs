namespace Forge.Codebase;

/// <summary>
/// One node in the codebase import graph: a file, its language,
/// and the module it belongs to (a module is a single .csproj /
/// project; files inside a project share its module id).
/// </summary>
public sealed record FileNode(string Path, string Language, string Module);

/// <summary>
/// One edge in the import graph: file A's `using` directives
/// reference file B. For TypeScript: A's `import` references B.
/// For C# we use the `using` syntax; we do not yet model the
/// imports inside class bodies (no deep symbolic analysis).
/// </summary>
public sealed record ImportEdge(string From, string To);

/// <summary>
/// Project-level edge: project A has a ProjectReference to project B.
/// For C# we read <c>&lt;ProjectReference Include="..." /&gt;</c> from
/// the .csproj.
/// </summary>
public sealed record ProjectEdge(string FromProject, string ToProject);

/// <summary>
/// Full import graph for a repo root, plus the git sha it was built
/// from. The builder returns one of these per call.
/// </summary>
public sealed record CodebaseGraph(
    string RepoRoot,
    DateTime BuiltAt,
    string RepoSha,
    IReadOnlyList<FileNode> Files,
    IReadOnlyList<ImportEdge> Imports,
    IReadOnlyList<ProjectEdge> Projects);

/// <summary>
/// A small SQLite manifest row keyed by repo sha. The actual graph
/// JSON lives on disk at <c>.portHorizon/codebase-graph/&lt;sha&gt;.json</c>;
/// the manifest is just a "what we have cached" pointer.
/// </summary>
public sealed record CodebaseGraphCache(
    DateTime BuiltAt,
    string RepoSha,
    int FileCount,
    int EdgeCount,
    string DiskPath);