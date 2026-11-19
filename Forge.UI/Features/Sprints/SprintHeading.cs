using Forge.Dashboard.Features.View;

namespace Forge.Dashboard.Features.Sprints;

public static class SprintHeading
{
    public static string? Title(ViewSprint sprint) => NumberedObjective(sprint) ?? sprint.Name;

    public static string? Picker(ViewSprint sprint) => NumberedObjective(sprint) is { } number
        ? $"{number}: {sprint.Goal}" : sprint.Name;

    // SprintStore keeps a 60-character preview of the objective in generated names.
    // The hero already renders the complete objective directly below its title.
    private static string? NumberedObjective(ViewSprint sprint)
    {
        if (sprint.Name is not { } name || sprint.Goal is not { Length: > 60 } goal ||
            !name.StartsWith("Sprint ", StringComparison.Ordinal))
            return null;
        var separator = name.IndexOf(": ", StringComparison.Ordinal);
        if (separator <= "Sprint ".Length || !int.TryParse(name.AsSpan("Sprint ".Length, separator - "Sprint ".Length), out _))
            return null;
        return name[(separator + 2)..] == goal[..60].TrimEnd() ? name[..separator] : null;
    }
}
