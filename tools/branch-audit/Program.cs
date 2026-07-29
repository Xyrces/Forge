// Forge.Tools.BranchAudit
//
// Enumerate remote branches, capture per-branch metadata, and produce:
//   - docs/BRANCH_AUDIT.md   (deterministic, human-readable table)
//   - docs/branch-audit.json (machine-readable sidecar; consumed by task-35)
//
// Scope of this tool: enumerate + classify + capture. It does NOT delete
// anything; the prune step is owned by task-35.
//
// Pure classification / protection logic lives in BranchClassifier and
// BranchProtector so it can be unit-tested without git or network access.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Forge.Tools.BranchAudit;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var opts = AuditOptions.Parse(args);
        if (opts is null)
        {
            AuditOptions.PrintUsage();
            return 2;
        }

        if (!Directory.Exists(opts.ClonePath))
        {
            Console.Error.WriteLine($"--clone path does not exist: {opts.ClonePath}");
            return 2;
        }

        Console.Error.WriteLine($"[branch-audit] clone={opts.ClonePath} default-branch={opts.DefaultBranch}");

        await RunGitAsync(opts.ClonePath, "fetch", "--prune", "origin");
        var heads = (await RunGitAsync(opts.ClonePath, "ls-remote", "--heads", "origin"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                var parts = line.Split('\t', 2, StringSplitOptions.TrimEntries);
                return (Sha: parts[0], Ref: parts[1]);
            })
            .Where(t => t.Ref.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(t => (Sha: t.Sha, Branch: t.Ref["refs/heads/".Length..]))
            .ToList();

        Console.Error.WriteLine($"[branch-audit] enumerated {heads.Count} heads");

        var configuredProtection = await TryLoadConfiguredProtectionAsync(opts.ClonePath);
        var rows = new List<AuditRow>(heads.Count);
        foreach (var (sha, branch) in heads)
        {
            var category = BranchClassifier.Classify(branch);
            var protected_ = BranchProtector.IsProtected(branch, configuredProtection);
            string? lastCommit;
            try
            {
                lastCommit = (await RunGitAsync(opts.ClonePath, "log", "-1", "--format=%cI", sha)).Trim();
                if (string.IsNullOrEmpty(lastCommit)) lastCommit = null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[branch-audit] warn: log {sha} on {branch} failed: {ex.Message}");
                lastCommit = null;
            }
            bool merged;
            try
            {
                await RunGitAsync(opts.ClonePath,
                    "merge-base", "--is-ancestor", sha, $"origin/{opts.DefaultBranch}");
                merged = true;
            }
            catch
            {
                merged = false;
            }
            rows.Add(new AuditRow(
                Branch: branch,
                Category: category,
                TipSha: sha,
                LastCommitDate: lastCommit,
                MergedIntoMain: merged,
                Protected: protected_));
        }

        var sorted = rows
            .OrderByDescending(r => r.Protected)
            .ThenBy(r => r.Category, StringComparer.Ordinal)
            .ThenByDescending(r => ParseDateOrMin(r.LastCommitDate))
            .ThenBy(r => r.Branch, StringComparer.Ordinal)
            .ToList();

        await WriteMarkdownAsync(opts.OutputMd, sorted, opts.DefaultBranch);
        await WriteJsonAsync(opts.OutputJson, sorted, opts.DefaultBranch);

        Console.Error.WriteLine($"[branch-audit] wrote {opts.OutputMd} and {opts.OutputJson} ({sorted.Count} rows)");
        return 0;
    }

    private static DateTime ParseDateOrMin(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : DateTime.MinValue;

    private static async Task<HashSet<string>> TryLoadConfiguredProtectionAsync(string clonePath)
    {
        // We can't talk to GitHub from this tool without injecting the token
        // (which the secrets skill says to never embed in code or commit it
        // anywhere). We keep the seam: a future task can pass a JSON file with
        // GitHub's branch-protection list. For now, this returns an empty set;
        // the always-protected names (main/master/develop/HEAD) are enforced
        // unconditionally by BranchProtector regardless.
        var envFile = Environment.GetEnvironmentVariable("BRANCH_AUDIT_PROTECTION_FILE");
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(envFile) || !File.Exists(envFile)) return set;
        try
        {
            var doc = JsonDocument.Parse(await File.ReadAllTextAsync(envFile));
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[branch-audit] warn: failed to read protection file {envFile}: {ex.Message}");
        }
        return set;
    }

    internal static async Task<string> RunGitAsync(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git");
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {p.ExitCode}: {stderr.Trim()}");
        }
        return stdout;
    }

    private static async Task WriteMarkdownAsync(string path, IReadOnlyList<AuditRow> rows, string defaultBranch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Branch audit");
        sb.AppendLine();
        sb.AppendLine($"Generated by `tools/branch-audit`. Source of truth for the zombie-branch prune (task-35).");
        sb.AppendLine($"- Remote: `origin`");
        sb.AppendLine($"- Default branch: `{defaultBranch}`");
        sb.AppendLine($"- Total branches: **{rows.Count}**");
        var byCat = rows.GroupBy(r => r.Category).OrderBy(g => g.Key, StringComparer.Ordinal);
        sb.Append("- By category: ");
        sb.AppendLine(string.Join(", ", byCat.Select(g => $"`{g.Key}`={g.Count()}")));
        var protectedCount = rows.Count(r => r.Protected);
        var mergedCount = rows.Count(r => r.MergedIntoMain);
        sb.AppendLine($"- Protected (never deleted): **{protectedCount}**");
        sb.AppendLine($"- Merged into `origin/{defaultBranch}`: **{mergedCount}**");
        sb.AppendLine();
        sb.AppendLine("Sort order: protected first, then by category, then by last-commit-date descending, then by branch name.");
        sb.AppendLine();
        sb.AppendLine("| branch | category | tip_sha | last_commit_date | merged_into_main | protected |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var r in rows)
        {
            sb.Append("| `").Append(r.Branch).Append("` | ");
            sb.Append(r.Category).Append(" | `");
            sb.Append(r.TipSha.Length >= 7 ? r.TipSha[..7] : r.TipSha).Append("` | ");
            sb.Append(string.IsNullOrEmpty(r.LastCommitDate) ? "—" : r.LastCommitDate).Append(" | ");
            sb.Append(r.MergedIntoMain ? "yes" : "no").Append(" | ");
            sb.Append(r.Protected ? "**yes**" : "no").AppendLine(" |");
        }
        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private static async Task WriteJsonAsync(string path, IReadOnlyList<AuditRow> rows, string defaultBranch)
    {
        var dto = new AuditDocument(
            GeneratedAt: DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            DefaultBranch: defaultBranch,
            Count: rows.Count,
            Rows: rows.Select(r => new AuditRowDto(
                Branch: r.Branch,
                Category: r.Category,
                TipSha: r.TipSha,
                LastCommitDate: r.LastCommitDate,
                MergedIntoMain: r.MergedIntoMain,
                Protected: r.Protected)).ToList());
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dto, opts));
    }
}

public sealed record AuditRow(
    string Branch,
    string Category,
    string TipSha,
    string? LastCommitDate,
    bool MergedIntoMain,
    bool Protected);

public sealed record AuditRowDto(
    string Branch,
    string Category,
    string TipSha,
    string? LastCommitDate,
    bool MergedIntoMain,
    bool Protected);

public sealed record AuditDocument(
    string GeneratedAt,
    string DefaultBranch,
    int Count,
    List<AuditRowDto> Rows);

public sealed class AuditOptions
{
    public required string ClonePath { get; init; }
    public required string OutputMd { get; init; }
    public required string OutputJson { get; init; }
    public required string DefaultBranch { get; init; }

    public static AuditOptions? Parse(string[] args)
    {
        string? clone = null;
        string? md = null;
        string? json = null;
        string def = "main";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--clone" when i + 1 < args.Length:
                    clone = args[++i];
                    break;
                case "--output-md" when i + 1 < args.Length:
                    md = args[++i];
                    break;
                case "--output-json" when i + 1 < args.Length:
                    json = args[++i];
                    break;
                case "--default-branch" when i + 1 < args.Length:
                    def = args[++i];
                    break;
                case "-h" or "--help":
                    PrintUsage();
                    return null;
                default:
                    Console.Error.WriteLine($"unknown arg: {args[i]}");
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(clone)) clone = Directory.GetCurrentDirectory();
        if (string.IsNullOrWhiteSpace(md)) md = "docs/BRANCH_AUDIT.md";
        if (string.IsNullOrWhiteSpace(json)) json = "docs/branch-audit.json";
        return new AuditOptions
        {
            ClonePath = clone,
            OutputMd = md,
            OutputJson = json,
            DefaultBranch = def,
        };
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: branch-audit [--clone <path>] [--output-md <path>] [--output-json <path>] [--default-branch <name>]");
    }
}

/// <summary>
/// Pure classifier. Maps a branch name (without refs/heads/ prefix) into one
/// of the six dead-fleet buckets or "other". Order of checks matters when
/// prefixes overlap (e.g. agent/task-1 must match agent, not "other").
/// </summary>
public static class BranchClassifier
{
    public const string Polecat  = "polecat";
    public const string Convoy   = "convoy";
    public const string Gt       = "gt";
    public const string Ph       = "ph";
    public const string Agent    = "agent";
    public const string StalePor = "POR-stale";
    public const string Other    = "other";

    private static readonly Regex PolecatRx  = new(@"^polecat/", RegexOptions.Compiled);
    private static readonly Regex ConvoyRx   = new(@"^convoy/", RegexOptions.Compiled);
    private static readonly Regex GtRx       = new(@"^gt\d", RegexOptions.Compiled);
    private static readonly Regex PhRx       = new(@"^ph([-/]|$)", RegexOptions.Compiled);
    private static readonly Regex AgentRx    = new(@"^agent/", RegexOptions.Compiled);
    private static readonly Regex StalePorRx = new(@"^POR-", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Classify(string branch)
    {
        if (PolecatRx.IsMatch(branch))  return Polecat;
        if (ConvoyRx.IsMatch(branch))   return Convoy;
        if (GtRx.IsMatch(branch))       return Gt;
        if (PhRx.IsMatch(branch))       return Ph;
        if (AgentRx.IsMatch(branch))    return Agent;
        if (StalePorRx.IsMatch(branch)) return StalePor;
        return Other;
    }
}

/// <summary>
/// Pure protection predicate. Branches matching main/master/develop/HEAD are
/// always protected, plus anything in the optional configured set (loaded
/// from GitHub's branch-protection endpoint in a follow-up; the seam is here
/// so the unit tests can drive it directly).
/// </summary>
public static class BranchProtector
{
    private static readonly HashSet<string> AlwaysProtected = new(StringComparer.Ordinal)
    {
        "main", "master", "develop", "HEAD",
    };

    public static bool IsProtected(string branch, IReadOnlySet<string> configured)
    {
        if (AlwaysProtected.Contains(branch)) return true;
        return configured.Contains(branch);
    }
}
