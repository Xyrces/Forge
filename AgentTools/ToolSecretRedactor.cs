using System.Text;

namespace Forge.AgentTools;

/// <summary>Redacts known credential values from tool output and diagnostic messages; not a process sandbox.</summary>
internal sealed class ToolSecretRedactor
{
    private readonly string[] _values;
    public ToolSecretRedactor(IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var entries = environment.ToArray();
        var values = entries.Where(x => x.Key.StartsWith("FORGE_SECRET_", StringComparison.OrdinalIgnoreCase)
            || x.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || x.Key.EndsWith("_PAT", StringComparison.OrdinalIgnoreCase)
            || x.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) || x.Key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
            || x.Key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) || x.Key.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value).Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList();
        var user = entries.FirstOrDefault(x => x.Key == "GITHUB_PACKAGES_USER").Value;
        var pat = entries.FirstOrDefault(x => x.Key == "GITHUB_PACKAGES_PAT").Value;
        if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pat)) values.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pat)));
        _values = values.SelectMany(v => new[] { v, Uri.EscapeDataString(v) }).Distinct().OrderByDescending(v => v.Length).ToArray();
    }
    public string Redact(string value)
    {
        foreach (var secret in _values) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }
}
