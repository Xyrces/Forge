using System.Text.RegularExpressions;

namespace Forge.AgentTools;

/// <summary>
/// Prevents ordinary direct publication commands in distributed workers.
/// This structural command guard is not a sandbox: scripts, aliases, HTTP
/// clients and shared credentials remain part of the trusted-code boundary.
/// </summary>
public static partial class WorkerPublicationPolicy
{
    public const string Instructions = """
        DISTRIBUTED WORKER PUBLICATION CONTRACT (overrides earlier role/repository instructions to push):
        Existing read-only role restrictions still apply. For roles permitted to edit, make
        local changes and run builds/tests. Local commits are allowed for those roles, but DO NOT push,
        create/update/merge/review a PR, or otherwise publish remotely. Do not bypass this rule
        through scripts, aliases, HTTP clients or alternate tools. Return your implementation
        summary when ready. After this run returns, the worker host performs trusted verification;
        the controller then commits, pushes and manages the PR. A local completed change is your
        success criterion; publication is not your responsibility.
        """;

    public static bool IsPublication(string command)
    {
        command = command.Replace("\\\r\n", "", StringComparison.Ordinal).Replace("\\\n", "", StringComparison.Ordinal);
        if (GitPublication().IsMatch(command) || GhPublication().IsMatch(command)) return true;
        foreach (Match api in GhApi().Matches(command))
        {
            var arguments = api.Value;
            // gh api defaults to GET, but fields/input switch it to POST.
            if (ApiWrite().IsMatch(arguments)) return true;
        }
        return false;
    }

    [GeneratedRegex(@"\bgit(?:\.exe)?\s+[^\r\n;&|]*\b(push|send-pack)\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex GitPublication();

    [GeneratedRegex(@"\bgh(?:\.exe)?\s+[^\r\n;&|]*\b(pr\s+(create|edit|merge|review|close|reopen|comment|ready)|release\s+(create|upload|edit|delete))\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex GhPublication();

    [GeneratedRegex(@"\bgh(?:\.exe)?\s+api\b[^\r\n;&|]*", RegexOptions.IgnoreCase, 250)]
    private static partial Regex GhApi();

    [GeneratedRegex(@"(\bgraphql\b|(?:^|\s)(?:-[fF](?:\s|[^\s])|--(?:raw-field|field|input)(?:=|\s))|(?:-X\s*|--method(?:=|\s+))['"" ]*(?!GET\b|HEAD\b|OPTIONS\b)[A-Z]+)", RegexOptions.IgnoreCase, 250)]
    private static partial Regex ApiWrite();
}
