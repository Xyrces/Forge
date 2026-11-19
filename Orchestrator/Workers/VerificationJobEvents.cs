using System.Net;
using System.Text.Json;
using Forge.Core.Execution;
using Forge.Core.Messaging;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator.Workers;

/// <summary>Wake the normal schedulers when Kubernetes finishes an integration verifier.
/// Job events are hints. The worker result, GitHub decisions and completion proofs remain authoritative.</summary>
internal sealed class VerificationJobEvents(HttpClient cluster, string jobNamespace, string datasetId,
    Func<string, CancellationToken, Task<WorkerAssignment?>> findAttempt,
    IEventPublisher events, ILogger<VerificationJobEvents> logger)
{
    internal const string Label = "forge-integrated-head";
    private readonly string collection = $"apis/batch/v1/namespaces/{Uri.EscapeDataString(jobNamespace)}/jobs?labelSelector={Label}%3Dtrue";

    public async Task RunAsync(CancellationToken ct)
    {
        string? version = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (version is null)
                {
                    using var list = await cluster.GetAsync(collection, ct);
                    list.EnsureSuccessStatusCode();
                    using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct));
                    foreach (var job in page.RootElement.GetProperty("items").EnumerateArray())
                        await ObserveAsync(job, ct);
                    version = ResourceVersion(page.RootElement);
                }
                using var watch = await cluster.GetAsync(collection + "&watch=true&timeoutSeconds=240&resourceVersion="
                    + Uri.EscapeDataString(version), HttpCompletionOption.ResponseHeadersRead, ct);
                if (watch.StatusCode == HttpStatusCode.Gone) { version = null; continue; }
                watch.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await watch.Content.ReadAsStreamAsync(ct));
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    using var message = JsonDocument.Parse(line);
                    var kind = message.RootElement.GetProperty("type").GetString();
                    var item = message.RootElement.GetProperty("object");
                    if (kind == "ERROR")
                    {
                        if (item.TryGetProperty("code", out var code) && code.GetInt32() == 410)
                        { version = null; break; }
                        throw new IOException("Kubernetes could not continue the verification Job watch.");
                    }
                    if (kind is "ADDED" or "MODIFIED" or "DELETED") await ObserveAsync(item, ct);
                    version = ResourceVersion(item);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Verification Job notifications disconnected; reconnecting from the last delivered event");
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
    }

    private static string ResourceVersion(JsonElement item) =>
        item.GetProperty("metadata").GetProperty("resourceVersion").GetString()
            ?? throw new InvalidDataException("Kubernetes response lacks a resource version.");

    internal async Task ObserveAsync(JsonElement job, CancellationToken ct)
    {
        if (!job.TryGetProperty("status", out var status) || !status.TryGetProperty("conditions", out var conditions)
            || !conditions.EnumerateArray().Any(c => c.GetProperty("status").GetString() == "True"
                && c.GetProperty("type").GetString() is "Complete" or "Failed")) return;
        var metadata = job.GetProperty("metadata");
        if (!metadata.TryGetProperty("labels", out var labels)
            || !labels.TryGetProperty("forge-attempt", out var rawId) || rawId.GetString() is not { Length: > 0 } id
            || metadata.GetProperty("name").GetString() != "forge-agent-" + id) return;
        var attempt = await findAttempt(id, ct);
        if (attempt is null || attempt.DatasetId != datasetId || attempt.Role != "QA"
            || !attempt.DispatchId.StartsWith("integration-verify-", StringComparison.Ordinal)) return;

        foreach (var kind in new[] { SweepKind.Watch, SweepKind.Assemble })
            await events.PublishAsync(new SweepTick
            {
                MessageId = $"verification-job:{id}:{kind}", ProjectId = attempt.ProjectId, Kind = kind,
            }, ct);
    }
}
