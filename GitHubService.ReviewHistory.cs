using System.Text.RegularExpressions;
using Octokit;

namespace Forge;

public sealed record ReviewCodeExcerpt(string HeadSha, string Path, int Line, int EndLine, string Code, string Url);
public sealed record ReviewHistoryFinding(string Path, int Line, int EndLine, string Body, ReviewCodeExcerpt? ReportedCode,
    ReviewCodeExcerpt? LaterCode, string? VerificationNote);
public sealed record LegacyReviewHistory(int Round, string? HeadSha, string Verdict, string Summary, string Reviewer,
    string Url, DateTimeOffset SubmittedAt, IReadOnlyList<ReviewHistoryFinding> Findings, string SourceKind = "legacy-comment");
public sealed record LegacyReviewHistoryPage(IReadOnlyList<LegacyReviewHistory> Reviews, bool Limited, IReadOnlyList<string> Notes, IReadOnlyList<NativeReviewHistory>? NativeReviews = null);

public sealed record NativeReviewComment(long Id, string Author, string Path, string Body, string DiffHunk, string Url,
    string? OriginalHead, long? InReplyToId);
public sealed record NativeReviewHistory(long Id, string? HeadSha, string Verdict, string Summary, string Reviewer,
    string? Url, DateTimeOffset? SubmittedAt, string Role, IReadOnlyList<NativeReviewComment> Comments);

public partial class GitHubService
{
    public virtual async Task<LegacyReviewHistoryPage> ReadReviewHistoryAsync(int prNumber, CancellationToken ct)
    {
        var notes = new List<string>();
        LegacyReviewHistoryPage legacy;
        try { legacy = await ReadLegacyReviewHistoryAsync(prNumber, ct); }
        catch (ApiException) { legacy = new([], false, ["Historical PR comments could not be loaded; cached task evidence remains available."]); }
        notes.AddRange(legacy.Notes);
        IReadOnlyList<NativeReviewHistory> native = [];
        try
        {
            var options = new ApiOptions { PageSize = 100, PageCount = 1, StartPage = 1 };
            var reviews = await _client.PullRequest.Review.GetAll(_owner, _repo, prNumber, options).WaitAsync(ct);
            var comments = await _client.PullRequest.ReviewComment.GetAll(_owner, _repo, prNumber, options).WaitAsync(ct);
            native = reviews.Select(r => new NativeReviewHistory(r.Id, r.CommitId, r.State.Value.ToString(), r.Body ?? "",
                r.User?.Login ?? "Author not recorded", r.HtmlUrl, r.SubmittedAt,
                !string.IsNullOrEmpty(ReviewerLogin) && string.Equals(r.User?.Login, ReviewerLogin, StringComparison.OrdinalIgnoreCase) ? "Forge reviewer App"
                    : r.User?.Type == AccountType.Bot ? "GitHub bot" : "GitHub participant",
                comments.Where(c => c.PullRequestReviewId == r.Id).Select(c => new NativeReviewComment(c.Id,
                    c.User?.Login ?? "Author not recorded", c.Path, c.Body ?? "", c.DiffHunk ?? "", c.HtmlUrl,
                    c.OriginalCommitId, c.InReplyToId)).ToArray())).OrderByDescending(r => r.Id).ToArray();
            if (reviews.Count == 100 || comments.Count == 100) notes.Add("Native history is limited to the first 100 reviews and 100 inline comments. Open GitHub for the complete discussion.");
        }
        catch (ApiException) { notes.Add("Native GitHub reviews could not be loaded; historical comments and cached task evidence remain available."); }
        return new(legacy.Reviews, legacy.Limited || notes.Count > legacy.Notes.Count, notes, native);
    }

    public virtual async Task<LegacyReviewHistoryPage> ReadLegacyReviewHistoryAsync(int prNumber, CancellationToken ct)
    {
        var comments = await _client.Issue.Comment.GetAllForIssue(_owner, _repo, prNumber,
            new ApiOptions { PageSize = 100, PageCount = 1, StartPage = 1 }).WaitAsync(ct);
        var commits = await _client.PullRequest.Commits(_owner, _repo, prNumber).WaitAsync(ct);
        var rows = new List<LegacyReviewHistory>();
        var notes = new List<string>();
        var excerptReads = 0;
        foreach (var comment in comments)
        {
            var body = comment.Body ?? "";
            var heading = Regex.Match(body, @"^\*\*\[Forge Reviewer [—-] round (\d+)\]\*\* (Approve|RequestChanges)\s", RegexOptions.CultureInvariant);
            if (!heading.Success || !int.TryParse(heading.Groups[1].Value, out var round)) continue;
            if (rows.Count >= 20) { notes.Add("Only the first 20 legacy review decisions are shown."); break; }
            var shaToken = Regex.Match(body, @"`([a-fA-F0-9]{7,40})`").Groups[1].Value;
            var matches = commits.Where(c => shaToken.Length >= 7 && c.Sha.StartsWith(shaToken, StringComparison.OrdinalIgnoreCase)).ToArray();
            var sha = matches.Length == 1 ? matches[0].Sha : null;
            var locations = new List<ReviewHistoryFinding>();
            foreach (Match location in Regex.Matches(body, @"`([^`\r\n:]+):([0-9,–\-]+)`"))
            {
                var path = location.Groups[1].Value;
                if (path.StartsWith('/') || path.Split('/').Contains("..")) continue;
                foreach (var range in location.Groups[2].Value.Split(','))
                {
                    if (locations.Count >= 4) break;
                    var ends = range.Split(['-', '–']);
                    if (!int.TryParse(ends[0], out var start) || start < 1 || start > int.MaxValue - 11) continue;
                    var end = ends.Length > 1 && int.TryParse(ends[1], out var parsed) ? parsed : start;
                    if (end < start) continue;
                    var excerpt = sha is null || excerptReads++ >= 16 ? null : await ReadReviewExcerptAsync(sha, path, start, Math.Min(end, start + 11), ct);
                    locations.Add(new(path, start, end, "Location cited in the original review comment.", excerpt, null,
                        excerpt is null ? "Code could not be verified at this revision. Use the original comment." : "Location reconstructed from the comment and verified against repository content; this was not a native GitHub review thread."));
                }
            }
            rows.Add(new(round, sha, heading.Groups[2].Value, body,
                comment.User?.Login ?? "Author not recorded", comment.HtmlUrl, comment.CreatedAt, locations));
        }
        for (var i = 0; i < rows.Count - 1; i++)
        {
            var next = rows.Skip(i + 1).FirstOrDefault(r => r.HeadSha is not null && r.HeadSha != rows[i].HeadSha);
            if (next?.HeadSha is not { } laterSha) continue;
            var updated = new List<ReviewHistoryFinding>();
            foreach (var finding in rows[i].Findings)
                updated.Add(finding with { LaterCode = excerptReads++ >= 16 ? null : await ReadReviewExcerptAsync(laterSha, finding.Path, finding.Line, Math.Min(finding.EndLine, finding.Line + 11), ct) });
            rows[i] = rows[i] with { Findings = updated };
        }
        if (excerptReads > 16) notes.Add("Source excerpts are limited to 16 reads; original comment links remain available.");
        if (comments.Count == 100) notes.Add("Legacy history is limited to the first 100 PR comments.");
        return new(rows, comments.Count == 100 || rows.Count == 20 || excerptReads > 16, notes);
    }

    public virtual async Task<ReviewCodeExcerpt?> ReadReviewExcerptAsync(string sha, string path, int first, int last, CancellationToken ct)
    {
        if (!Regex.IsMatch(sha, "^[a-fA-F0-9]{40}$") || first < 1 || last < first || last - first > 80
            || path.StartsWith('/') || path.Split('/').Contains("..")) return null;
        try
        {
            var files = await _client.Repository.Content.GetAllContentsByRef(_owner, _repo, path, sha).WaitAsync(ct);
            var file = files.SingleOrDefault(f => f.Type == ContentType.File && f.Path == path && f.Size <= 200_000);
            if (file?.Content is not { } content) return null;
            var lines = content.Replace("\r\n", "\n").Split('\n');
            if (last > lines.Length) return null;
            var url = $"https://github.com/{Uri.EscapeDataString(_owner)}/{Uri.EscapeDataString(_repo)}/blob/{sha}/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + $"#L{first}-L{last}";
            return new(sha, path, first, last, string.Join('\n', lines.Skip(first - 1).Take(last - first + 1)), url);
        }
        catch (NotFoundException) { return null; }
    }
}
