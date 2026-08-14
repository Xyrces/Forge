using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Dashboard;

public static class SecretsEndpoints
{
    public static IEndpointRouteBuilder MapSecretsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/projects/{id}/secrets");

        group.MapGet("/", ListSecretsAsync)
             .WithName("ListProjectSecrets")
             .WithSummary("List the kinds of secrets stored for a project. Ciphertext is never returned — the operator sees 'set' or 'unset' per kind.");

        group.MapPost("/", UpsertSecretAsync)
             .WithName("UpsertProjectSecret")
             .WithSummary("Set a secret value for a project. Ciphertext is encrypted with IDataProtector before storage. Per-kind singleton: re-POSTing replaces the existing value.");

        group.MapDelete("/{kind}", DeleteSecretAsync)
             .WithName("DeleteProjectSecret")
             .WithSummary("Delete a stored secret. After deletion the orchestrator falls back to the global env/appsettings value for that kind.");

        return endpoints;
    }

    public sealed record SecretMetadataDto(
        string Kind,
        bool Set,
        DateTime? CreatedAt,
        DateTime? UpdatedAt,
        bool Known = false);

    public sealed record SetSecretRequest(string Kind, string Value);

    private static async Task<IResult> ListSecretsAsync(
        string id,
        [FromServices] ISecretStore store,
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        var secrets = await store.ListAsync(id, ct);
        var byKind = secrets.ToDictionary(s => s.Kind, s => s, StringComparer.OrdinalIgnoreCase);

        // Known kinds: the fixed operational kinds + every CONFIGURED
        // LLM provider's key (same convention ProviderApiKeyResolver
        // resolves at runtime), so a configured provider's secret
        // always shows its set/unset state in the upper panel. Custom
        // kinds the operator stored via POST follow, sorted, so the
        // lower panel can render everything that exists. The DTO
        // carries Known so the UI ships no mirror of this list.
        var knownKinds = new List<string> { SecretKinds.GitHubToken, SecretKinds.MeshyApiKey };
        if (services.GetService(typeof(Forge.Agents.LlmConfig)) is Forge.Agents.LlmConfig llm)
        {
            knownKinds.AddRange(llm.Providers.Select(p => SecretKinds.ForProvider(p.Name)));
        }
        else
        {
            knownKinds.AddRange(new[] { SecretKinds.KiloGatewayApiKey, SecretKinds.KimiApiKey, SecretKinds.MinimaxApiKey });
        }
        var known = knownKinds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var dtos = known.Select(kind => byKind.TryGetValue(kind, out var row)
            ? new SecretMetadataDto(kind, Set: true, row.CreatedAt, row.UpdatedAt, Known: true)
            : new SecretMetadataDto(kind, Set: false, null, null, Known: true))
            .Concat(byKind.Keys
                .Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k =>
                {
                    var row = byKind[k];
                    return new SecretMetadataDto(row.Kind, Set: true, row.CreatedAt, row.UpdatedAt, Known: false);
                }));
        return Results.Ok(dtos);
    }

    private static async Task<IResult> UpsertSecretAsync(
        string id,
        SetSecretRequest? body,
        [FromServices] ISecretStore store,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Kind) || string.IsNullOrEmpty(body.Value))
            return Results.BadRequest(new { error = "kind and value are required" });
        if (!IsValidKind(body.Kind))
            return Results.BadRequest(new { error = "kind must match [a-z0-9][a-z0-9_-]* (1-64 chars); it becomes an env-var suffix FORGE_SECRET_<KIND>" });
        if (body.Value.Length > 8192)
            return Results.BadRequest(new { error = "value too long (>8KB)" });
        var logger = loggerFactory.CreateLogger("Secrets.Upsert");
        var plaintext = System.Text.Encoding.UTF8.GetBytes(body.Value);
        var rec = await store.UpsertAsync(new NewSecret(id, body.Kind, plaintext), ct);
        logger.LogInformation("Set secret '{Kind}' for project '{ProjectId}'", rec.Kind, rec.ProjectId);
        return Results.Ok(new SecretMetadataDto(rec.Kind, Set: true, rec.CreatedAt, rec.UpdatedAt));
    }

    private static async Task<IResult> DeleteSecretAsync(
        string id,
        string kind,
        [FromServices] ISecretStore store,
        CancellationToken ct)
    {
        var removed = await store.DeleteAsync(id, kind, ct);
        return removed ? Results.NoContent() : Results.NotFound(new { error = "secret not set" });
    }

    /// <summary>
    /// Custom kinds become env-var suffixes (<c>FORGE_SECRET_&lt;KIND&gt;</c>)
    /// in agent bash sessions, so constrain to lowercase slug shape.
    /// </summary>
    private static bool IsValidKind(string kind)
    {
        if (kind.Length < 1 || kind.Length > 64) return false;
        if (!char.IsAsciiLetterLower(kind[0]) && !char.IsDigit(kind[0])) return false;
        return kind.All(c => char.IsAsciiLetterLower(c) || char.IsDigit(c) || c == '_' || c == '-');
    }
}
