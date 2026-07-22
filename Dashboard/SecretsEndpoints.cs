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
        DateTime? UpdatedAt);

    public sealed record SetSecretRequest(string Kind, string Value);

    private static async Task<IResult> ListSecretsAsync(
        string id,
        [FromServices] ISecretStore store,
        CancellationToken ct)
    {
        var secrets = await store.ListAsync(id, ct);
        // Whitelist of known kinds so the UI always shows the same
        // set of fields (even when unset). Operators add more
        // kinds by editing the list below.
        var knownKinds = new[] { SecretKinds.GitHubToken, SecretKinds.KiloGatewayApiKey, SecretKinds.MeshyApiKey };
        var byKind = secrets.ToDictionary(s => s.Kind, s => s, StringComparer.OrdinalIgnoreCase);
        var dtos = knownKinds.Select(kind => byKind.TryGetValue(kind, out var row)
            ? new SecretMetadataDto(kind, Set: true, row.CreatedAt, row.UpdatedAt)
            : new SecretMetadataDto(kind, Set: false, null, null));
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
}
