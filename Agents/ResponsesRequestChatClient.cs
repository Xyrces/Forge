#pragma warning disable OPENAI001 // Pinned SDK Responses adapter; covered by offline wire-contract tests.
using Forge.Core;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Forge.Agents;

/// <summary>Resolved attribution for each client; request policies may add attempt identity from execution scope.</summary>
public sealed record LlmRequestContext(string Provider, string Model, AgentType Role, string? ProjectId, int MaxOutputTokens);

/// <summary>Stateless Responses requests with bounded output and resumable encrypted reasoning.</summary>
internal sealed class ResponsesRequestChatClient(IChatClient inner, ProviderConfig provider, string model, IDisposable? transportOwner = null) : DelegatingChatClient(inner)
{
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) transportOwner?.Dispose();
    }
    internal ChatOptions Prepare(ChatOptions? original)
    {
        var options = original?.Clone() ?? new ChatOptions();
        options.MaxOutputTokens = Math.Min(options.MaxOutputTokens ?? provider.MaxOutputTokens ?? 8192,
            provider.MaxOutputTokens ?? 8192);
        if (options.ModelId is { } requested && !string.Equals(requested, model, StringComparison.Ordinal))
            throw new InvalidOperationException("Model changes require a new factory client and admission context.");
        options.ModelId = model;
        options.ConversationId = null;
        if (!string.IsNullOrWhiteSpace(provider.ReasoningEffort))
        {
            if (!Enum.TryParse<ReasoningEffort>(provider.ReasoningEffort, true, out var effort))
                throw new InvalidOperationException($"Invalid reasoning effort '{provider.ReasoningEffort}' for {provider.Name}.");
            options.Reasoning = new ReasoningOptions { Effort = effort };
        }
        // Sampling controls are unsupported with reasoning enabled. Omit rather than inherit extractor defaults.
        if (options.Reasoning?.Effort != ReasoningEffort.None)
        {
            options.Temperature = null;
            options.TopP = null;
        }
        if (options.RawRepresentationFactory is not null)
            throw new InvalidOperationException("Raw Responses options cannot bypass Forge request policy.");
        options.RawRepresentationFactory = _ =>
        {
            var raw = new CreateResponseOptions { StoredOutputEnabled = false, BackgroundModeEnabled = false, ServiceTier = "default" };
            raw.IncludedProperties.Add("reasoning.encrypted_content");
            return raw;
        };
        return options;
    }

    private static void CaptureBillingEvidence(object? raw, ResponsesInputAdmission? scope)
    {
        if (scope is null) return;
        var response = raw switch
        {
            ResponseResult result => result,
            StreamingResponseCompletedUpdate completed => completed.Response,
            StreamingResponseIncompleteUpdate incomplete => incomplete.Response,
            StreamingResponseFailedUpdate failed => failed.Response,
            _ => null,
        };
        if (response is null) return;
        // Persist only billing evidence, never prompts, output text or encrypted reasoning.
        // JSON mode retains SDK-unknown fields such as cache-write TTL usage.
        using var document = JsonDocument.Parse(ModelReaderWriter.Write(response).ToMemory());
        var root = document.RootElement;
        var evidence = new Dictionary<string, JsonElement>();
        foreach (var key in new[] { "id", "model", "service_tier", "status", "usage" })
            if (root.TryGetProperty(key, out var value)) evidence[key] = value;
        scope.BillingEvidenceJson = JsonSerializer.Serialize(evidence);
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var scope = ResponsesInputAdmission.Current;
        var response = await InnerClient.GetResponseAsync(messages, Prepare(options), cancellationToken);
        CaptureBillingEvidence(response.RawRepresentation, scope);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scope = ResponsesInputAdmission.Current;
        await foreach (var update in InnerClient.GetStreamingResponseAsync(messages, Prepare(options), cancellationToken))
        {
            CaptureBillingEvidence(update.RawRepresentation, scope);
            yield return update;
        }
    }
}
