namespace Forge.Dashboard.Services;

public static class GatePresentation
{
    public static string Label(IReadOnlyDictionary<string, string>? values, string stage, bool loading) =>
        values is not null && values.TryGetValue(stage, out var state) && state is "hold" or "open"
            ? state == "hold" ? "held" : "open"
            : loading ? "loading" : "unknown";
}
