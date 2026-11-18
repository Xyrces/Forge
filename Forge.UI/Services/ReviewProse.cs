using System.Text.RegularExpressions;

namespace Forge.Dashboard.Services;

/// <summary>Display-only cleanup of Forge protocol. Original GitHub text and review authority are untouched.</summary>
public static class ReviewProse
{
    public static string? Legacy(string? text, int round, string verdict)
    {
        if (text is null) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var header = Regex.Match(lines.FirstOrDefault() ?? "", @"^\*\*\[Forge Reviewer [—-] round (\d+)\]\*\* (Approve|RequestChanges)\s*$");
        var terminal = verdict == "Approve" ? "REVIEWER_VERDICT: APPROVE" : "REVIEWER_VERDICT: REQUEST_CHANGES";
        return header.Success && header.Groups[1].Value == round.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && header.Groups[2].Value == verdict && lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) == terminal
            ? Format(text, true) : text;
    }

    public static string? Format(string? text, bool forgeGenerated)
    {
        if (!forgeGenerated || text is null) return text;
        var output = new List<string>();
        char fence = '\0'; var length = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            // Markdown quoted/indented source is not transport syntax.
            var marker = Regex.Match(line, @"^ {0,3}(`{3,}|~{3,})(.*)$");
            if (marker.Success)
            {
                var run = marker.Groups[1].Value;
                if (fence == '\0') { fence = run[0]; length = run.Length; }
                else if (run[0] == fence && run.Length >= length && string.IsNullOrWhiteSpace(marker.Groups[2].Value)) fence = '\0';
                output.Add(line); continue;
            }
            if (fence != '\0' || line.StartsWith('>') || line.StartsWith("    ") || line.StartsWith('\t')) { output.Add(line); continue; }
            if (Regex.IsMatch(line, @"^REVIEWER_VERDICT:\s*(APPROVE|REQUEST_CHANGES|ERROR)\s*$")
                || Regex.IsMatch(line, @"^\*\*\[Forge Reviewer [—-] round \d+\]\*\* (Approve|RequestChanges)\s*$")
                || Regex.IsMatch(line, @"^<!-- forge-review:v1:[a-f0-9]{64} -->\s*$")) continue;
            output.Add(line.StartsWith("REVIEWER_NOTES: ", StringComparison.Ordinal) ? line[16..] : line);
        }
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[0])) output.RemoveAt(0);
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1])) output.RemoveAt(output.Count - 1);
        return string.Join('\n', output);
    }
}
