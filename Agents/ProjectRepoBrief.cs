using System.Text;

namespace Forge.Agents;

/// <summary>
/// A compact brief about a project's checked-out repo, injected into
/// agent prompts so they don't interrogate the operator about facts
/// the codebase already answers (observed live 2026-08-09: the
/// talaria intake agent asked the operator what the tech stack was —
/// the clone sits right there). Built from the local clone: stack
/// detection from well-known manifests, a top-level tree, the README
/// head, and a docs/ listing. Filesystem-only, no LLM, cheap enough
/// to rebuild on a short cache window.
/// </summary>
public static class ProjectRepoBrief
{
    /// <summary>Build the brief, or a "(unavailable)" marker when the
    /// root is missing (clone failed — the bootstrap scaffolds).</summary>
    public static string Build(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return "(repo unavailable — no local clone)";

        var sb = new StringBuilder();
        sb.AppendLine("Tech stack (detected from manifests): " + DetectStack(root));
        sb.AppendLine();
        sb.AppendLine("Top-level layout:");
        AppendTree(root, sb);
        var readme = ReadReadme(root);
        if (readme is not null)
        {
            sb.AppendLine();
            sb.AppendLine("README (excerpt):");
            sb.AppendLine(readme);
        }
        var docs = ListDocs(root);
        if (docs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("docs/: " + string.Join(", ", docs));
        }
        return sb.ToString();
    }

    internal static string DetectStack(string root)
    {
        var parts = new List<string>();
        var slns = Directory.GetFiles(root, "*.sln*").Length;
        var csprojs = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Count(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        if (slns > 0 || csprojs > 0)
        {
            parts.Add($".NET / C# ({(slns > 0 ? $"{slns} solution(s), " : "")}{csprojs} project(s))");
        }
        if (File.Exists(Path.Combine(root, "package.json"))) parts.Add("Node.js (package.json)");
        if (File.Exists(Path.Combine(root, "Cargo.toml"))) parts.Add("Rust (Cargo.toml)");
        if (File.Exists(Path.Combine(root, "go.mod"))) parts.Add("Go (go.mod)");
        if (File.Exists(Path.Combine(root, "pyproject.toml"))
            || File.Exists(Path.Combine(root, "requirements.txt"))) parts.Add("Python");
        if (File.Exists(Path.Combine(root, "pom.xml"))
            || File.Exists(Path.Combine(root, "build.gradle"))
            || File.Exists(Path.Combine(root, "build.gradle.kts"))) parts.Add("JVM (Maven/Gradle)");
        return parts.Count > 0 ? string.Join("; ", parts) : "unknown — no well-known manifests at the root";
    }

    private static void AppendTree(string root, StringBuilder sb)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", "bin", "obj", "node_modules", ".forge", ".portHorizon", ".vs", ".idea" };
        var count = 0;
        try
        {
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (skip.Contains(name)) continue;
                sb.AppendLine($"  {name}/");
                if (++count >= 40) { sb.AppendLine("  …"); return; }
            }
            foreach (var file in Directory.GetFiles(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(25))
            {
                sb.AppendLine($"  {Path.GetFileName(file)}");
            }
        }
        catch (IOException) { sb.AppendLine("  (unreadable)"); }
        catch (UnauthorizedAccessException) { sb.AppendLine("  (unreadable)"); }
    }

    private static string? ReadReadme(string root)
    {
        var readme = Directory.GetFiles(root, "README*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (readme is null) return null;
        try
        {
            var text = File.ReadAllText(readme);
            var lines = text.Split('\n').Take(60);
            var excerpt = string.Join('\n', lines).TrimEnd();
            return excerpt.Length > 2500 ? excerpt[..2500] + "\n…[truncated]…" : excerpt;
        }
        catch (IOException) { return null; }
    }

    private static List<string> ListDocs(string root)
    {
        var docs = Path.Combine(root, "docs");
        if (!Directory.Exists(docs)) return new List<string>();
        try
        {
            return Directory.GetFiles(docs, "*.md", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).Where(n => n is not null).Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        }
        catch (IOException) { return new List<string>(); }
    }
}
