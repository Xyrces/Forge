using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.AgentTools;

/// <summary>
/// P5.1 — Native SharedContext equivalent. Reads the full body of
/// an artifact by id so MAF agents can pull a single artifact
/// into context on demand rather than have the orchestrator
/// inline every artifact body into every prompt.
///
/// <para>
/// Id prefixes:
/// <list type="bullet">
///   <item><c>design-</c> — design_artifact body (mesh / texture
///   / animation / rig body, markdown)</item>
///   <item><c>spec-</c> — spec body (markdown). Returns the most
///   recent version of the spec.</item>
///   <item><c>art-</c> — art_output body (file path, e.g.
///   <c>spec-1/calc.glb</c>). Returns the relative path; the
///   consuming agent's <c>bash</c> tool reads the file.</item>
/// </list>
/// </para>
///
/// <para>
/// Returns <c>null</c> if the id doesn't match a known prefix
/// or no row exists. The orchestrator's <c>context_handoff</c>
/// table records every successful read so we can audit which
/// artifacts each agent actually consumed.
/// </para>
/// </summary>
public sealed class ArtifactReadTool
{
    private readonly DesignArtifactStore _designArtifacts;
    private readonly ISpecStore _specs;
    private readonly ArtOutputStore _artOutputs;
    private readonly ContextHandoffStore? _handoffs;
    private readonly ILogger<ArtifactReadTool>? _logger;

    public ArtifactReadTool(
        DesignArtifactStore designArtifacts,
        ISpecStore specs,
        ArtOutputStore artOutputs,
        ContextHandoffStore? handoffs = null,
        ILogger<ArtifactReadTool>? logger = null)
    {
        _designArtifacts = designArtifacts;
        _specs = specs;
        _artOutputs = artOutputs;
        _handoffs = handoffs;
        _logger = logger;
    }

    /// <summary>
    /// Read a full artifact body by id. Returns the body as a
    /// JSON envelope so the LLM sees a structured response (id +
    /// kind + body). When the id doesn't resolve, returns the
    /// JSON <c>{"error":"not_found","id":"..."}</c> envelope.
    /// </summary>
    [Description("Read a full artifact body by id. Use this when the spec index references an artifact and you need the full content. Recognized prefixes: 'design-' (design artifacts), 'spec-' (spec body), 'art-' (art outputs). Returns the body as a JSON envelope.")]
    public async Task<string> ReadArtifactAsync(
        [Description("Artifact id. Examples: 'design-abc123', 'spec-task-1', 'art-foo-001'.")] string artifactId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
        {
            return JsonSerializer.Serialize(new { error = "empty_id" });
        }

        string? body = null;
        string kind = "unknown";
        if (artifactId.StartsWith("design-", StringComparison.Ordinal))
        {
            var row = await _designArtifacts.GetAsync(artifactId, cancellationToken);
            if (row is not null)
            {
                body = row.Body;
                kind = "design";
            }
        }
        else if (artifactId.StartsWith("spec-", StringComparison.Ordinal))
        {
            var spec = await _specs.GetAsync(artifactId, cancellationToken);
            if (spec is not null)
            {
                body = spec.Body;
                kind = "spec";
            }
        }
        else if (artifactId.StartsWith("art-", StringComparison.Ordinal))
        {
            var row = await _artOutputs.GetAsync(artifactId, cancellationToken);
            if (row is not null)
            {
                // The body of an art_output is a relative file
                // path. Returning the path (not the file content)
                // is the contract: the agent uses its `bash` tool
                // to read the file.
                body = row.Body;
                kind = "art";
            }
        }

        if (body is null)
        {
            _logger?.LogWarning("ArtifactRead: id={Id} not found", artifactId);
            return JsonSerializer.Serialize(new { error = "not_found", id = artifactId });
        }

        // Log the read for the context_handoff lineage (best-effort;
        // the store may be null in tests).
        if (_handoffs is not null)
        {
            try
            {
                await _handoffs.LogReadAsync(artifactId, kind, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ArtifactRead: context_handoff log failed for {Id}", artifactId);
            }
        }

        _logger?.LogInformation("ArtifactRead: id={Id} kind={Kind} bytes={Bytes}", artifactId, kind, body.Length);
        return JsonSerializer.Serialize(new
        {
            id = artifactId,
            kind,
            body,
        });
    }

    /// <summary>
    /// MAF-friendly wrapper. Same signature but uses an
    /// AIFunctionFactory so MAF can call it as a tool. The
    /// agent passes the artifact id; we return the JSON envelope.
    /// </summary>
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        ([Description("Artifact id. Examples: 'design-abc123', 'spec-task-1', 'art-foo-001'.")] string artifactId)
            => ReadArtifactAsync(artifactId, CancellationToken.None),
        name: "read_artifact",
        description: "Read a full artifact body by id. Returns JSON envelope {id, kind, body}. Recognized prefixes: 'design-', 'spec-', 'art-'.");
}
