using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core.Control;
using Forge.Core.Db;
using Microsoft.Extensions.AI;

namespace Forge.Orchestrator.Control;

/// <summary>Reserve durable worst-case liability before every HTTP generation, including streaming.</summary>
public static class BudgetRequestPolicy
{
    // Qualification checked 2026-11-17 UTC against:
    // https://developers.openai.com/api/docs/models/gpt-6.1-sol.md
    // https://developers.openai.com/api/docs/guides/prompt-caching.md
    // Global Standard tier: full context is a deliberately conservative input
    // ceiling, including opaque SDK reasoning replay. $10/M input covers the
    // long-context multiplier and longest cache-write rate; $15/M output covers
    // the long-context rate. New models/tiers require explicit qualification.
    internal const string QualifiedModel = "gpt-6.1-sol";
    internal const int QualifiedContextTokens = 1_050_000;
    internal const int QualifiedMaxOutputTokens = 128_000;
    internal const decimal MinimumInputUsdPerMillion = 10m;
    internal const decimal MinimumOutputUsdPerMillion = 15m;

    public static Func<LlmRequestContext, IChatClient, IChatClient> Create(AgentOptions options,
        IDbConnectionFactory registry, Func<CancellationToken, Task> ensureAdmission) =>
        (context, client) => new BudgetChatClient(client, new DurableBudgetStore(registry),
            options, context, ensureAdmission);

    internal sealed class BudgetChatClient(IChatClient inner, IDurableBudgetStore ledger,
        AgentOptions options, LlmRequestContext context, Func<CancellationToken, Task> ensureAdmission)
        : DelegatingChatClient(inner)
    {
        private ChatOptions BoundedOptions(ChatOptions? original)
        {
            var bounded = original?.Clone() ?? new ChatOptions();
            if (bounded.ModelId is { } requested && !string.Equals(requested, context.Model, StringComparison.Ordinal))
                throw new InvalidOperationException("Model changes require a separately qualified admission context.");
            bounded.MaxOutputTokens = Math.Min(QualifiedMaxOutputTokens,
                Math.Min(context.MaxOutputTokens, bounded.MaxOutputTokens ?? context.MaxOutputTokens));
            return bounded;
        }

        private async Task<(string RequestId, ModelPriceOptions Price)> ReserveAsync(
            IReadOnlyList<ChatMessage> messages, ChatOptions? chatOptions, CancellationToken ct)
        {
            if (!options.Budget.PaidCallsEnabled)
                throw new InvalidOperationException("Paid model calls are disabled in this deployment.");
            var provider = options.Llm.Providers.SingleOrDefault(p => p.Name == context.Provider);
            if (provider is null || !string.Equals(provider.Api, "responses", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The budgeted Forge pilot requires an explicitly configured Responses provider.");
            if (options.Headroom.Enabled || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != "https" || endpoint.Host != "api.openai.com" || !endpoint.IsDefaultPort
                || endpoint.AbsolutePath.TrimEnd('/') != "/v1" || endpoint.Query.Length != 0
                || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
                throw new InvalidOperationException("The qualified spending profile requires the direct global OpenAI endpoint.");
            if (!string.Equals(context.Model, QualifiedModel, StringComparison.Ordinal)
                || !options.Budget.Models.TryGetValue(context.Model, out var price)
                || price.InputUsdPerMillion < MinimumInputUsdPerMillion
                || price.OutputUsdPerMillion < MinimumOutputUsdPerMillion
                || price.MaximumInputTokens < QualifiedContextTokens)
                throw new InvalidOperationException("Model pricing/context is below the qualified full-protocol spending bound.");
            // Keep the reservation and settlement on the same price snapshot.
            price = new ModelPriceOptions
            {
                InputUsdPerMillion = price.InputUsdPerMillion,
                OutputUsdPerMillion = price.OutputUsdPerMillion,
                MaximumInputTokens = price.MaximumInputTokens,
            };
            if (chatOptions?.RawRepresentationFactory is not null || chatOptions?.ConversationId is not null)
                throw new InvalidOperationException("Budgeted calls must use explicit bounded stateless input.");
            // Text/function-only Forge pilot. Images and hosted tools need their own reviewed cost bounds.
            if (messages.SelectMany(m => m.Contents).Any(c => c is DataContent or UriContent))
                throw new InvalidOperationException("Media inputs are not enabled in the Forge-first spending profile.");
            // This size estimate rejects obviously excessive explicit payloads;
            // it is NOT the liability bound. RawRepresentation may carry opaque
            // provider reasoning/history omitted by generic JSON serialization.
            long explicitInputBytes = 4096;
            explicitInputBytes += System.Text.Encoding.UTF8.GetByteCount(chatOptions?.Instructions ?? "");
            if (chatOptions?.ResponseFormat is { } format)
                explicitInputBytes += JsonSerializer.SerializeToUtf8Bytes(format, format.GetType()).LongLength;
            foreach (var message in messages)
                explicitInputBytes += JsonSerializer.SerializeToUtf8Bytes(message).LongLength;
            foreach (var tool in chatOptions?.Tools ?? [])
            {
                if (tool is not AIFunction function)
                    throw new InvalidOperationException("Hosted tools require an explicit additional spending policy.");
                explicitInputBytes += JsonSerializer.SerializeToUtf8Bytes(function.JsonSchema).LongLength
                    + System.Text.Encoding.UTF8.GetByteCount(function.Name + function.Description) + 256;
            }
            if (explicitInputBytes > price.MaximumInputTokens)
                throw new InvalidOperationException("Explicit input exceeds the pilot payload size limit.");
            var outputBound = Math.Min(context.MaxOutputTokens, chatOptions?.MaxOutputTokens ?? context.MaxOutputTokens);
            if (outputBound <= 0) throw new InvalidOperationException("A positive output bound is required.");
            var bound = (price.MaximumInputTokens * price.InputUsdPerMillion + outputBound * price.OutputUsdPerMillion) / 1_000_000m;
            if (bound > options.Budget.MaximumRequestUsd)
                throw new InvalidOperationException("Request worst-case cost exceeds the configured per-request ceiling.");
            await ensureAdmission(ct);
            var requestId = "req-" + Guid.NewGuid().ToString("N");
            var request = new BudgetReservationRequest(options.Controller.DatasetId, requestId,
                context.ProjectId ?? options.Bootstrap.ProjectId,
                string.IsNullOrEmpty(options.Worker.AttemptId) ? options.Controller.InstanceId : options.Worker.AttemptId,
                context.Provider, context.Model, bound);
            var reservation = await ledger.ReserveAsync(request, ct);
            if (!reservation.Admitted || !reservation.IsNew)
                throw new InvalidOperationException("Model request denied by durable budget admission: " + reservation.Reason);
            return (requestId, price);
        }

        private async Task SettleAsync(string requestId, ModelPriceOptions price, UsageDetails? usage)
        {
            // Settle at conservative qualified rates, not an exact invoice estimate.
            // Charge all reported input at the upper rate; no assumed cache discount.
            // Missing or interrupted usage remains reserved, never silently refunded.
            decimal? cost = usage?.InputTokenCount is { } input && usage.OutputTokenCount is { } output
                ? (input * price.InputUsdPerMillion + output * price.OutputUsdPerMillion) / 1_000_000m : null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!await ledger.CompleteAsync(options.Controller.DatasetId, requestId, cost, timeout.Token))
                throw new InvalidOperationException("Model usage could not be reconciled with its reservation.");
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
        {
            var input = messages.ToArray();
            chatOptions = BoundedOptions(chatOptions);
            var (request, price) = await ReserveAsync(input, chatOptions, cancellationToken);
            UsageDetails? usage = null;
            try
            {
                await ensureAdmission(cancellationToken);
                var response = await InnerClient.GetResponseAsync(input, chatOptions, cancellationToken);
                usage = response.Usage;
                return response;
            }
            finally { await SettleAsync(request, price, usage); }
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var input = messages.ToArray();
            chatOptions = BoundedOptions(chatOptions);
            var (request, price) = await ReserveAsync(input, chatOptions, cancellationToken);
            UsageDetails? usage = null;
            try
            {
                await ensureAdmission(cancellationToken);
                await foreach (var update in InnerClient.GetStreamingResponseAsync(input, chatOptions, cancellationToken))
                {
                    foreach (var terminal in update.Contents.OfType<UsageContent>()) usage = terminal.Details;
                    yield return update;
                }
            }
            finally { await SettleAsync(request, price, usage); }
        }
    }
}
