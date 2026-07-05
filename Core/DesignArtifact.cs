namespace Forge.Core;

/// <summary>
/// A design artifact: a visual (or visual-language) document
/// produced by the Designer agent and attached to a spec.
/// Renders inline in the dashboard (HTML iframe, SVG, or markdown).
///
/// <para>
/// Four kinds:
/// <list type="bullet">
///   <item><b>wireframe</b> — low-fidelity HTML. Defines the layout
///   and the user-facing elements of a screen. Used by the
///   engineering agents as the visual source of truth.</item>
///   <item><b>mockup</b> — high-fidelity HTML. Used when the
///   operator wants to approve the visual before engineering
///   starts. Reuses wireframe structure; adds typography,
///   color, motion references.</item>
///   <item><b>component-spec</b> — markdown table. Defines the
///   props, states, and accessibility requirements of a single
///   UI component. Used by clientdev as the contract for a
///   component implementation.</item>
///   <item><b>visual-rule</b> — markdown. Defines a project-wide
///   visual convention (color, spacing, motion, etc.) that
///   other artifacts must follow. Loaded by the Designer on
///   every run via <c>db_get_visual_language</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Artifacts are versioned via <see cref="ParentArtifactId"/>: a
/// new artifact that supersedes an old one carries the old id
/// in the parent field. The dashboard renders the chain.
/// </para>
/// </summary>
public sealed record DesignArtifact(
    string Id,
    string SpecId,
    DesignArtifactKind Kind,
    string Title,
    string Body,
    string BodyKind,                  // "html" | "svg" | "markdown"
    string? ReferencesJson,           // JSON array of {designArtifactId, why}
    string? ParentArtifactId,
    DesignArtifactStatus Status,
    string Author,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public enum DesignArtifactKind
{
    Wireframe,
    Mockup,
    ComponentSpec,
    VisualRule,
}

public static class DesignArtifactKindExtensions
{
    public static string ToDbValue(this DesignArtifactKind k) => k switch
    {
        DesignArtifactKind.Wireframe => "wireframe",
        DesignArtifactKind.Mockup => "mockup",
        DesignArtifactKind.ComponentSpec => "component-spec",
        DesignArtifactKind.VisualRule => "visual-rule",
        _ => "wireframe",
    };

    public static bool TryParseDb(string s, out DesignArtifactKind kind)
    {
        switch (s)
        {
            case "wireframe": kind = DesignArtifactKind.Wireframe; return true;
            case "mockup": kind = DesignArtifactKind.Mockup; return true;
            case "component-spec": kind = DesignArtifactKind.ComponentSpec; return true;
            case "visual-rule": kind = DesignArtifactKind.VisualRule; return true;
            default: kind = DesignArtifactKind.Wireframe; return false;
        }
    }
}

public enum DesignArtifactStatus
{
    Draft,
    Approved,
    Superseded,
}