using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Octokit;

namespace Forge.Reviewer;

public sealed record ReviewFinding(string Path, int Line, string Side, string Body);
public sealed record ReviewThreadResolution(string ThreadId, string LastCommentId, string Reason);
public sealed record ReviewDraft(int Round, string HeadSha, string Verdict, string Summary, List<ReviewFinding> Findings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ReviewThreadResolution>? Resolutions = null);
public sealed record PublishedReview(int Round, string HeadSha, string Verdict, string Summary, string Reviewer, string Url,
    DateTimeOffset? SubmittedAt, List<ReviewFinding> Findings);
public sealed record FormalDecision(string Login, string Head, PullRequestReviewState State, bool Automated,
    long Id, string Body, string Url);
public sealed record EffectiveReview(bool AutomatedApproved, bool HumanApproved, bool OutstandingHumanObjection,
    IReadOnlyList<FormalDecision> CurrentRequests)
{
    public static EffectiveReview Evaluate(IEnumerable<FormalDecision> all, string head, string? nominatedHuman = null, string? author = null)
    {
        var active = all.Where(r => !string.Equals(r.Login, author, StringComparison.OrdinalIgnoreCase) && (r.Automated || !r.Login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))).Where(r => r.State is PullRequestReviewState.Approved or PullRequestReviewState.ChangesRequested or PullRequestReviewState.Dismissed)
            .GroupBy(r => r.Login, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(r => r.Id)!).Where(r => r.State != PullRequestReviewState.Dismissed).ToArray();
        var current = active.Where(r => r.Head == head).ToArray();
        return new(current.Any(r => r.Automated && r.State == PullRequestReviewState.Approved),
            current.Any(r => !r.Automated && r.State == PullRequestReviewState.Approved
                && (string.IsNullOrWhiteSpace(nominatedHuman) || string.Equals(r.Login, nominatedHuman, StringComparison.OrdinalIgnoreCase))),
            active.Any(r => !r.Automated && r.State == PullRequestReviewState.ChangesRequested),
            current.Where(r => r.State == PullRequestReviewState.ChangesRequested).ToArray());
    }
}

internal static partial class ReviewFindings
{
    internal static string Marker(ReviewDraft draft) => "<!-- forge-review:v1:" + Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(draft, Json))) + " -->";
    internal static ReviewerVerdict ParseVerdict(string? output)
    {
        var last = output?.Split('\n').LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
        return last?.ToUpperInvariant() switch
        {
            "REVIEWER_VERDICT: APPROVE" => ReviewerVerdict.Approve,
            "REVIEWER_VERDICT: REQUEST_CHANGES" => ReviewerVerdict.RequestChanges,
            _ => ReviewerVerdict.Error
        };
    }
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const string Instructions = "End with REVIEWER_VERDICT: APPROVE or REVIEWER_VERDICT: REQUEST_CHANGES on a line of its own. "
        + "Before the verdict, put each actionable line finding on one separate line as REVIEWER_FINDING: "
        + "{\"path\":\"repository/relative/file\",\"line\":42,\"side\":\"RIGHT\",\"body\":\"What must change and why\"}. "
        + "Use LEFT for a removed line. Explain findings without a diff anchor in the assessment. "
        + "For each eligible unresolved review conversation, verify the original finding against the current code. "
        + "If corrected, emit REVIEWER_RESOLVED_THREAD: {\"threadId\":\"id\",\"lastCommentId\":\"id\",\"reason\":\"Specific correction and verification evidence\"}. "
        + "Resolve only conversations explicitly marked eligible. Every eligible thread needs either a verified resolution or REQUEST_CHANGES. "
        + "Human conversations remain open for their participants.";

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex Hunk();

    public static ReviewDraft Create(int round, string sha, string verdict, string output, string diff,
        IReadOnlyList<ReviewConversation>? conversations = null, string? reviewer = null)
    {
        var anchors = new HashSet<(string Path, int Line, string Side)>();
        string? left = null, right = null;
        int oldLine = 0, newLine = 0;
        bool hunk = false;
        foreach (var row in diff.Replace("\r", "").Split('\n'))
        {
            if (row.StartsWith("diff --git ", StringComparison.Ordinal)) { hunk = false; left = right = null; continue; }
            if (!hunk && row.StartsWith("--- a/", StringComparison.Ordinal)) { left = row[6..]; continue; }
            if (!hunk && row.StartsWith("+++ b/", StringComparison.Ordinal)) { right = row[6..]; continue; }
            var match = Hunk().Match(row);
            if (match.Success)
            {
                oldLine = int.Parse(match.Groups[1].Value); newLine = int.Parse(match.Groups[2].Value);
                hunk = true; continue;
            }
            if (!hunk || row.Length == 0) continue;
            if (row[0] is ' ' or '-') { if (left is not null) anchors.Add((right ?? left, oldLine, "LEFT")); oldLine++; }
            if (row[0] is ' ' or '+') { if (right is not null) anchors.Add((right, newLine, "RIGHT")); newLine++; }
        }
        List<ReviewFinding> findings = [];
        List<ReviewThreadResolution> resolutions = [];
        List<string> summary = [];
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("REVIEWER_VERDICT:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("REVIEWER_RESOLVED_THREAD:", StringComparison.OrdinalIgnoreCase))
            {
                var resolution = JsonSerializer.Deserialize<ReviewThreadResolution>(line["REVIEWER_RESOLVED_THREAD:".Length..], Json)
                    ?? throw new InvalidOperationException("Review thread resolution has no evidence.");
                var thread = conversations?.SingleOrDefault(t => t.Id == resolution.ThreadId);
                if (thread is null || !GitHubService.CanResolveConversation(thread, reviewer)
                    || thread.LastCommentId != resolution.LastCommentId || string.IsNullOrWhiteSpace(resolution.Reason))
                    throw new InvalidOperationException("Review thread resolution does not match an eligible verified conversation.");
                if (resolutions.Any(r => r.ThreadId == resolution.ThreadId))
                    throw new InvalidOperationException("Review thread resolution is duplicated.");
                resolutions.Add(resolution);
                continue;
            }
            if (line.StartsWith("REVIEWER_FINDING:", StringComparison.OrdinalIgnoreCase))
            {
                ReviewFinding? finding;
                try { finding = JsonSerializer.Deserialize<ReviewFinding>(line["REVIEWER_FINDING:".Length..], Json); }
                catch (JsonException) { finding = null; }
                if (finding is not null && !string.IsNullOrWhiteSpace(finding.Body))
                {
                    if (anchors.Contains((finding.Path, finding.Line, finding.Side)) && findings.Count < 40 && !findings.Contains(finding))
                        findings.Add(finding);
                    else summary.Add($"{finding.Path}, line {finding.Line} — {finding.Body}");
                    continue;
                }
            }
            summary.Add(raw.Replace("REVIEWER_NOTES:", "", StringComparison.OrdinalIgnoreCase).TrimEnd());
        }
        var body = string.Join("\n", summary).Trim();
        if (verdict == nameof(ReviewerVerdict.Approve) && conversations is not null
            && conversations.Any(t => GitHubService.CanResolveConversation(t, reviewer) && !resolutions.Any(r => r.ThreadId == t.Id)))
            throw new InvalidOperationException("Approval omitted verification of an unresolved reviewer conversation.");
        return new(round, sha, verdict, body.Length == 0 ? verdict : body, findings,
            verdict == nameof(ReviewerVerdict.Approve) && resolutions.Count > 0 ? resolutions : null);
    }
}
