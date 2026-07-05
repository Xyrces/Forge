namespace Forge.Core;

/// <summary>
/// An art output: a produced art asset (3D mesh, texture,
/// animation, or rig) attached to a spec. The Artist agent
/// submits Meshy jobs and records the resulting <c>.glb</c>
/// under <c>.portHorizon/art-output/</c>.
///
/// <para>
/// Four kinds:
/// <list type="bullet">
///   <item><b>mesh</b> — a 3D model. The body column holds a
///   relative path to a <c>.glb</c> file. The dashboard
///   renders it via <c>&lt;model-viewer&gt;</c>.</item>
///   <item><b>texture</b> — a 2D image. The body column holds a
///   relative path to a <c>.png</c> file. The dashboard
///   renders it via <c>&lt;img&gt;</c>.</item>
///   <item><b>animation</b> — an animated 3D model or
///   <c>.mp4</c> turntable. The body column holds a
///   relative path.</item>
///   <item><b>rig</b> — a rigged model ready for animation.
///   The body column holds a relative path to a <c>.glb</c>
///   or <c>.fbx</c> rigged file.</item>
/// </list>
/// </para>
///
/// <para>
/// Outputs are versioned via <see cref="ParentArtifactId"/>: a
/// new output that supersedes an old one carries the old id
/// in the parent field. The dashboard renders the chain.
/// </para>
/// </summary>
public sealed record ArtOutput(
    string Id,
    string SpecId,
    ArtOutputKind Kind,
    string Title,
    string Body,                      // relative path under .portHorizon/art-output/
    string BodyKind,                  // "glb" | "fbx" | "obj" | "png" | "mp4" | "usdz"
    string? ReferencesJson,           // JSON array of {designArtifactId, meshyTaskId, why}
    string? ParentArtifactId,
    ArtOutputStatus Status,
    string Author,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public enum ArtOutputKind
{
    Mesh,
    Texture,
    Animation,
    Rig,
}

public static class ArtOutputKindExtensions
{
    public static string ToDbValue(this ArtOutputKind k) => k switch
    {
        ArtOutputKind.Mesh => "mesh",
        ArtOutputKind.Texture => "texture",
        ArtOutputKind.Animation => "animation",
        ArtOutputKind.Rig => "rig",
        _ => "mesh",
    };

    public static bool TryParseDb(string s, out ArtOutputKind kind)
    {
        switch (s)
        {
            case "mesh": kind = ArtOutputKind.Mesh; return true;
            case "texture": kind = ArtOutputKind.Texture; return true;
            case "animation": kind = ArtOutputKind.Animation; return true;
            case "rig": kind = ArtOutputKind.Rig; return true;
            default: kind = ArtOutputKind.Mesh; return false;
        }
    }
}

public enum ArtOutputStatus
{
    Draft,
    Approved,
    Superseded,
}
