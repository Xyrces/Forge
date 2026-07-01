namespace PortHorizon.Agents.Codebase;

/// <summary>
/// Builds a <see cref="CodebaseGraph"/> for a repo root. The builder
/// is incremental: pass the prior cache and the builder only re-parses
/// files changed since the prior <c>RepoSha</c>. Same sha → no work.
///
/// <para>
/// The interface is language-agnostic. Implementations plug in:
/// <list type="bullet">
///   <item><c>DotnetCodebaseGraphBuilder</c> for C# / .csproj.</item>
///   <item>(Phase 4+) <c>TypeScriptCodebaseGraphBuilder</c>.</item>
/// </list>
/// </para>
/// </summary>
public interface ICodebaseGraphBuilder
{
    /// <summary>
    /// Build (or refresh) the import graph for <paramref name="repoRoot"/>.
    /// </summary>
    /// <param name="repoRoot">Absolute path to the repo root.</param>
    /// <param name="priorCache">
    /// The last cache entry for this repo, if any. Pass <c>null</c>
    /// to do a full walk.
    /// </param>
    /// <param name="cacheDirectory">
    /// Directory to write the on-disk graph JSON to. Defaults to
    /// <c>.portHorizon/codebase-graph/</c> under the repo root.
    /// </param>
    Task<CodebaseGraph> BuildAsync(
        string repoRoot,
        CodebaseGraphCache? priorCache,
        string? cacheDirectory = null,
        CancellationToken ct = default);

    /// <summary>
    /// True if the builder supports the given language id. Lets
    /// callers mix multiple builders in a single repo (e.g. C#
    /// for the server, TypeScript for the dashboard).
    /// </summary>
    bool SupportsLanguage(string language);
}