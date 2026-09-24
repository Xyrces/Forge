using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Tools.E2E;

internal sealed record BenchmarkExternalCase(
    string Id,
    string Title,
    string Prompt,
    string RepositoryPath,
    string BaseCommit,
    IReadOnlyList<string> AllowedPaths)
{
    private static readonly Regex IdPattern = new(
        "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CommitPattern = new(
        "^[0-9a-fA-F]{40,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static BenchmarkExternalCase Load(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("External benchmark case path must be absolute.", nameof(path));
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("External benchmark case must be a JSON object.");
        var allowedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "title", "prompt", "repositoryPath", "baseCommit", "allowedPaths",
        };
        foreach (var property in root.EnumerateObject())
        {
            if (!allowedNames.Remove(property.Name))
                throw new InvalidDataException($"External benchmark case contains unknown or duplicate property '{property.Name}'.");
        }
        foreach (var required in new[] { "id", "title", "prompt", "repositoryPath", "baseCommit" })
        {
            if (allowedNames.Contains(required))
                throw new InvalidDataException($"External benchmark case is missing '{required}'.");
        }

        var id = RequiredString(root, "id");
        if (!IdPattern.IsMatch(id))
            throw new InvalidDataException("External benchmark case id contains unsupported characters.");
        var title = RequiredString(root, "title");
        var prompt = RequiredString(root, "prompt", allowFormattingWhitespace: true);
        var repositoryPath = RequiredString(root, "repositoryPath");
        if (!Path.IsPathFullyQualified(repositoryPath))
            throw new InvalidDataException("External benchmark repositoryPath must be absolute.");
        repositoryPath = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(repositoryPath))
            throw new DirectoryNotFoundException($"External benchmark repository does not exist: {repositoryPath}");
        var baseCommit = RequiredString(root, "baseCommit");
        if (!CommitPattern.IsMatch(baseCommit))
            throw new InvalidDataException("External benchmark baseCommit must be a full hexadecimal object id.");

        var allowedPaths = new List<string>();
        if (root.TryGetProperty("allowedPaths", out var paths))
        {
            if (paths.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("External benchmark allowedPaths must be an array.");
            foreach (var item in paths.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("External benchmark allowedPaths entries must be strings.");
                var normalized = NormalizeAllowedPath(item.GetString()!);
                if (!allowedPaths.Contains(normalized, StringComparer.Ordinal))
                    allowedPaths.Add(normalized);
            }
            if (allowedPaths.Count == 0)
                throw new InvalidDataException("External benchmark allowedPaths cannot be empty when present.");
        }
        return new(id, title, prompt, repositoryPath, baseCommit.ToLowerInvariant(), allowedPaths);
    }

    public bool Allows(string path)
    {
        var normalized = NormalizeRepositoryPath(path);
        if (IsForbidden(normalized)) return false;
        return AllowedPaths.Count == 0 || AllowedPaths.Contains(normalized, StringComparer.Ordinal);
    }

    public string PrepareSanitizedSnapshot(string workspaceRoot, string clone)
    {
        if (!string.Equals(CaptureGit(RepositoryPath, "rev-parse", "--is-inside-work-tree").Trim(), "true",
                StringComparison.Ordinal))
            throw new InvalidDataException("External benchmark repositoryPath is not a git worktree.");
        var sourceHead = CaptureGit(RepositoryPath, "rev-parse", "HEAD").Trim().ToLowerInvariant();
        if (!string.Equals(sourceHead, BaseCommit, StringComparison.Ordinal))
            throw new InvalidDataException($"External benchmark baseCommit must equal the prepared repository HEAD ({sourceHead}).");
        var sourceStatus = CaptureGit(RepositoryPath, "status", "--porcelain", "--untracked-files=all");
        if (!string.IsNullOrWhiteSpace(sourceStatus))
            throw new InvalidDataException("External benchmark repository must have a clean working tree.");

        var tree = CaptureGit(RepositoryPath, "ls-tree", "-r", "--full-tree", BaseCommit);
        foreach (var line in tree.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) throw new InvalidDataException("External benchmark git tree was malformed.");
            var metadata = line[..tab];
            var repositoryPath = NormalizeRepositoryPath(line[(tab + 1)..]);
            if (metadata.StartsWith("120000 ", StringComparison.Ordinal))
                throw new InvalidDataException($"External benchmark snapshot contains a symlink: {repositoryPath}");
            if (metadata.StartsWith("160000 ", StringComparison.Ordinal))
                throw new InvalidDataException($"External benchmark snapshot contains a submodule: {repositoryPath}");
            if (IsForbidden(repositoryPath))
                throw new InvalidDataException($"External benchmark snapshot contains forbidden state: {repositoryPath}");
        }

        RunGit(workspaceRoot, "clone", "--quiet", "--no-local", "--no-hardlinks", "--no-checkout", RepositoryPath, clone);
        RunGit(clone, "checkout", "--quiet", "--detach", BaseCommit);
        var importedHead = CaptureGit(clone, "rev-parse", "HEAD").Trim().ToLowerInvariant();
        if (!string.Equals(importedHead, BaseCommit, StringComparison.Ordinal))
            throw new InvalidOperationException("External benchmark clone did not resolve to the requested baseCommit.");
        var sourceTree = CaptureGit(clone, "rev-parse", $"{BaseCommit}^{{tree}}").Trim().ToLowerInvariant();
        RunGit(clone, "config", "user.email", "benchmark@local");
        RunGit(clone, "config", "user.name", "forge-benchmark");
        var rootCommit = CaptureGit(
            clone, "commit-tree", sourceTree, "-m", "sanitized benchmark base").Trim().ToLowerInvariant();
        RunGit(clone, "update-ref", "refs/heads/main", rootCommit);
        RunGit(clone, "symbolic-ref", "HEAD", "refs/heads/main");
        RunGit(clone, "reset", "--quiet", "--hard", rootCommit);
        RunGit(clone, "remote", "remove", "origin");
        foreach (var reference in CaptureGit(clone, "for-each-ref", "--format=%(refname)")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.Equals(reference, "refs/heads/main", StringComparison.Ordinal))
                RunGit(clone, "update-ref", "-d", reference);
        }
        RunGit(clone, "reflog", "expire", "--expire=now", "--all");
        RunGit(clone, "gc", "--prune=now");

        var sanitizedTree = CaptureGit(clone, "rev-parse", "HEAD^{tree}").Trim().ToLowerInvariant();
        var visibleCommits = CaptureGit(clone, "rev-list", "--all", "--count").Trim();
        var remotes = CaptureGit(clone, "remote").Trim();
        var unreachable = CaptureGit(clone, "fsck", "--unreachable", "--no-reflogs").Trim();
        if (!string.Equals(sourceTree, sanitizedTree, StringComparison.Ordinal)
            || visibleCommits != "1"
            || remotes.Length != 0
            || unreachable.Length != 0)
        {
            throw new InvalidOperationException(
                "External benchmark could not create an exact single-commit snapshot without source history.");
        }

        var sourceHeadAfter = CaptureGit(RepositoryPath, "rev-parse", "HEAD").Trim().ToLowerInvariant();
        var sourceStatusAfter = CaptureGit(RepositoryPath, "status", "--porcelain", "--untracked-files=all");
        if (!string.Equals(sourceHeadAfter, sourceHead, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(sourceStatusAfter))
            throw new InvalidOperationException("External benchmark source repository changed while preparing the snapshot.");
        return sourceHead;
    }

    public static string PatchSha256(string patchPath)
    {
        using var stream = File.OpenRead(patchPath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string RequiredString(
        JsonElement root,
        string property,
        bool allowFormattingWhitespace = false)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"External benchmark {property} must be a nonempty string.");
        var result = value.GetString()!;
        if (result.Any(character => char.IsControl(character)
                && (!allowFormattingWhitespace || character is not ('\r' or '\n' or '\t'))))
            throw new InvalidDataException($"External benchmark {property} cannot contain control characters.");
        return result;
    }

    private static string NormalizeAllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            throw new InvalidDataException("External benchmark allowedPaths entries must be nonempty.");
        var normalized = NormalizeRepositoryPath(path).TrimEnd('/');
        if (string.IsNullOrEmpty(normalized) || IsForbidden(normalized))
            throw new InvalidDataException($"External benchmark allowed path is unsafe: {path}");
        return normalized;
    }

    internal static string NormalizeRepositoryPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (Path.IsPathFullyQualified(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"External benchmark repository path is unsafe: {path}");
        return normalized;
    }

    private static bool IsForbidden(string path) => path.Split('/').Any(part =>
        part.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || part.Equals(".portHorizon", StringComparison.OrdinalIgnoreCase));

    private static void RunGit(string cwd, params string[] arguments)
    {
        var start = GitStart(cwd, arguments);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git for external benchmark preparation.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"External benchmark git command failed ({process.ExitCode}): {output}\n{error}");
    }

    private static string CaptureGit(string cwd, params string[] arguments)
    {
        var start = GitStart(cwd, arguments);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git for external benchmark inspection.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"External benchmark git inspection failed ({process.ExitCode}): {error}");
        return output;
    }

    private static ProcessStartInfo GitStart(string cwd, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
