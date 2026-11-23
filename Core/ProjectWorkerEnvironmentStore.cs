using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Forge.Core;

public sealed record ProjectWorkerEnvironment(
    string Revision = "0", string DefinitionRevision = "0",
    string? DockerfilePath = null, string BuildContext = ".",
    string? ApprovedImage = null, string? ApprovedSourceRevision = null,
    string? ApprovedDefinitionRevision = null,
    string? ApprovedDockerfilePath = null, string? ApprovedBuildContext = null)
{
    public string Status => DockerfilePath is null ? "stock"
        : ApprovedDefinitionRevision == DefinitionRevision ? "active" : "pending";
}

public sealed record SetWorkerEnvironmentDefinition(string ExpectedRevision, string? DockerfilePath, string? BuildContext = null);
public sealed record QualifyWorkerEnvironment(string ExpectedRevision, string DefinitionRevision, string Image, string SourceRevision);

public interface IProjectWorkerEnvironmentStore
{
    Task<ProjectWorkerEnvironment?> GetAsync(string projectId, CancellationToken ct = default);
    Task<ProjectWorkerEnvironment?> SetDefinitionAsync(string projectId, SetWorkerEnvironmentDefinition definition, CancellationToken ct = default);
    Task<ProjectWorkerEnvironment?> QualifyAsync(string projectId, QualifyWorkerEnvironment qualification, CancellationToken ct = default);
    Task<string?> ResolveImageAsync(string projectId, CancellationToken ct = default);
}

/// <summary>Only trusted operators may write this registry setting. Never source approvals from agent memory.</summary>
public sealed partial class ProjectWorkerEnvironmentStore(ProjectStore projects) : IProjectWorkerEnvironmentStore
{
    internal const string SettingsKey = "$workerEnvironment";

    internal static string InvalidateSourceBinding(string? raw)
    {
        var settings = raw is null ? new JsonObject() : JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidOperationException("Project settings must be a JSON object.");
        var previous = Read(settings);
        var revision = Guid.NewGuid().ToString("N");
        settings[SettingsKey] = JsonSerializer.SerializeToNode(new ProjectWorkerEnvironment(
            Revision: revision, DefinitionRevision: revision,
            DockerfilePath: previous.DockerfilePath, BuildContext: previous.BuildContext));
        return settings.ToJsonString();
    }

    public async Task<ProjectWorkerEnvironment?> GetAsync(string projectId, CancellationToken ct = default)
    {
        var settings = await projects.Settings.ReadAsync(projectId, ct);
        return settings.Exists ? Read(settings.Value) : null;
    }

    public async Task<string?> ResolveImageAsync(string projectId, CancellationToken ct = default)
        => (await GetAsync(projectId, ct))?.ApprovedImage;

    public async Task<ProjectWorkerEnvironment?> SetDefinitionAsync(string projectId, SetWorkerEnvironmentDefinition definition, CancellationToken ct = default)
    {
        var path = definition.DockerfilePath;
        var context = definition.BuildContext ?? ".";
        if (path is not null) ValidatePath(path, false);
        ValidatePath(context, true);
        if (path is not null && context != "." && !path.StartsWith(context + "/", StringComparison.Ordinal))
            throw new ArgumentException("Dockerfile must be inside the selected repository-relative build context.");
        if (path is null) context = ".";
        ProjectWorkerEnvironment? result = null;
        var found = await projects.Settings.MutateAsync(projectId, obj =>
        {
            var previous = Read(obj);
            RequireRevision(previous, definition.ExpectedRevision);
            if (previous.DockerfilePath == path && previous.BuildContext == context) { result = previous; return; }
            var revision = Guid.NewGuid().ToString("N");
            result = path is null ? new(Revision: revision, DefinitionRevision: revision)
                : previous with { Revision = revision, DefinitionRevision = revision, DockerfilePath = path, BuildContext = context };
            obj[SettingsKey] = JsonSerializer.SerializeToNode(result);
        }, ct);
        return found ? result : null;
    }

    public async Task<ProjectWorkerEnvironment?> QualifyAsync(string projectId, QualifyWorkerEnvironment qualification, CancellationToken ct = default)
    {
        if (qualification.Image is null || !ImmutableImage().IsMatch(qualification.Image))
            throw new ArgumentException("Image must be a repository name pinned with @sha256 and a 64-character lowercase digest.");
        if (qualification.SourceRevision is null || !CommitRevision().IsMatch(qualification.SourceRevision))
            throw new ArgumentException("Source revision must be a full 40-character Git commit hash.");
        ProjectWorkerEnvironment? result = null;
        var found = await projects.Settings.MutateAsync(projectId, obj =>
        {
            var previous = Read(obj);
            RequireRevision(previous, qualification.ExpectedRevision);
            if (previous.DockerfilePath is null || previous.DefinitionRevision != qualification.DefinitionRevision)
                throw new ProjectSettingsConflictException("The worker environment definition changed; rebuild the current definition.");
            result = previous with
            {
                Revision = Guid.NewGuid().ToString("N"), ApprovedImage = qualification.Image,
                ApprovedSourceRevision = qualification.SourceRevision.ToLowerInvariant(),
                ApprovedDefinitionRevision = previous.DefinitionRevision,
                ApprovedDockerfilePath = previous.DockerfilePath, ApprovedBuildContext = previous.BuildContext
            };
            obj[SettingsKey] = JsonSerializer.SerializeToNode(result);
        }, ct);
        return found ? result : null;
    }

    private static ProjectWorkerEnvironment Read(JsonObject obj)
        => obj[SettingsKey] is null ? new() : obj[SettingsKey]!.Deserialize<ProjectWorkerEnvironment>()
            ?? throw new InvalidOperationException("Invalid worker environment settings.");

    private static void RequireRevision(ProjectWorkerEnvironment current, string expected)
    {
        if (current.Revision != expected)
            throw new ProjectSettingsConflictException("Worker environment changed; reload and retry.");
    }

    private static void ValidatePath(string value, bool allowRoot)
    {
        if (allowRoot && value == ".") return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.StartsWith('/')
            || value.Contains('\\') || value.Contains(':') || value.Any(char.IsControl)
            || value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Use a repository-relative path without empty, dot, or parent segments.");
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?::[0-9]+)?(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*(?::[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127})?@sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImmutableImage();
    [GeneratedRegex(@"^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitRevision();
}
