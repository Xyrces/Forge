using System.Runtime.CompilerServices;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core.Control;
using Forge.Core.Db;
using Microsoft.Extensions.AI;

namespace Forge.Orchestrator.Control;

/// <summary>Reserve durable worst-case liability before every HTTP generation, including streaming.</summary>
public static class BudgetRequestPolicy
{
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
            bounded.MaxOutputTokens = Math.Min(OpenAiStandardPricing.MaxOutputTokens,
                Math.Min(context.MaxOutputTokens, bounded.MaxOutputTokens ?? context.MaxOutputTokens));
            return bounded;
        }

        private (OpenAiStandardPricing Tariff, ModelPriceOptions ReservationRates) Qualify(
            IReadOnlyList<ChatMessage> messages, ChatOptions? chatOptions)
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
            var tariff = OpenAiStandardPricing.ForModel(context.Model);
            if (tariff is null
                || !options.Budget.Models.TryGetValue(context.Model, out var price)
                || price.InputUsdPerMillion < tariff.MinimumInputUsdPerMillion
                || price.OutputUsdPerMillion < tariff.MinimumOutputUsdPerMillion
                || price.MaximumInputTokens < OpenAiStandardPricing.ContextTokens)
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
            // Byte size is a transport concern, not a token estimate. The native
            // handler bounds the complete serialized body (including opaque replay)
            // before exact counting; only that count controls token admission.
            if (chatOptions?.Tools?.Any(tool => tool is not AIFunction) == true)
                throw new InvalidOperationException("Hosted tools require an explicit additional spending policy.");
            return (tariff, price);
        }

        private async Task<string> ReserveAsync(ModelPriceOptions price, long inputTokens, int outputBound, CancellationToken ct)
        {
            if (inputTokens > OpenAiStandardPricing.MaxInputTokens)
                throw new InvalidOperationException("Counted input exceeds the qualified model input limit.");
            if (outputBound <= 0) throw new InvalidOperationException("A positive output bound is required.");
            var bound = (inputTokens * price.InputUsdPerMillion + outputBound * price.OutputUsdPerMillion) / 1_000_000m;
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
            return requestId;
        }

        private async Task SettleAsync(string requestId, OpenAiStandardPricing tariff, ModelPriceOptions price,
            UsageDetails? usage, ResponsesInputAdmission scope)
        {
            // Without category evidence, settle reported totals at the upper rates. Missing totals retain the hold.
            decimal? cost = usage?.InputTokenCount is >= 0 and var input && input <= scope.CountedInputTokens
                && usage.OutputTokenCount is >= 0 and var output && output <= scope.MaxOutputTokens
                ? (input * price.InputUsdPerMillion + output * price.OutputUsdPerMillion) / 1_000_000m : null;
            var basis = "legacy-conservative-bound-v1";
            if (scope.BillingEvidenceJson is { } evidence)
            {
                var estimate = tariff.Calculate(evidence, scope.CountedInputTokens, scope.MaxOutputTokens, out var unexpectedProfile);
                if (unexpectedProfile)
                {
                    cost = null;
                    basis = "uncertain:unexpected-model-or-tier";
                }
                else if (estimate is not null)
                {
                    cost = estimate.CostUsd;
                    basis = tariff.Version + (estimate.CompleteBreakdown ? ":usage-estimate" : ":cache-aware-upper-bound");
                }
                else basis = "conservative-fallback:incomplete-or-invalid-usage";
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!await ledger.CompleteWithUsageAsync(options.Controller.DatasetId, requestId, cost,
                    scope.BillingEvidenceJson, basis, timeout.Token))
                throw new InvalidOperationException("Model usage could not be reconciled with its reservation.");
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
        {
            var input = messages.ToArray();
            chatOptions = BoundedOptions(chatOptions);
            var (tariff, price) = Qualify(input, chatOptions);
            string? request = null;
            using var scope = new ResponsesInputAdmission(context.Model, chatOptions.MaxOutputTokens!.Value,
                OpenAiStandardPricing.ContextTokens, async (count, output, ct) =>
                {
                    request = await ReserveAsync(price, count, output, ct);
                    await ensureAdmission(ct);
                });
            UsageDetails? usage = null;
            try
            {
                await ensureAdmission(cancellationToken);
                var response = await InnerClient.GetResponseAsync(input, chatOptions, cancellationToken);
                if (!scope.Admitted) throw new InvalidOperationException("Generation bypassed exact input-token admission.");
                usage = response.Usage;
                return response;
            }
            finally { if (request is not null) await SettleAsync(request, tariff, price, usage, scope); }
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var input = messages.ToArray();
            chatOptions = BoundedOptions(chatOptions);
            var (tariff, price) = Qualify(input, chatOptions);
            string? request = null;
            using var scope = new ResponsesInputAdmission(context.Model, chatOptions.MaxOutputTokens!.Value,
                OpenAiStandardPricing.ContextTokens, async (count, output, ct) =>
                {
                    request = await ReserveAsync(price, count, output, ct);
                    await ensureAdmission(ct);
                });
            UsageDetails? usage = null;
            try
            {
                await ensureAdmission(cancellationToken);
                await foreach (var update in InnerClient.GetStreamingResponseAsync(input, chatOptions, cancellationToken))
                {
                    if (!scope.Admitted) throw new InvalidOperationException("Generation bypassed exact input-token admission.");
                    foreach (var terminal in update.Contents.OfType<UsageContent>()) usage = terminal.Details;
                    yield return update;
                }
            }
            finally { if (request is not null) await SettleAsync(request, tariff, price, usage, scope); }
        }
    }
}
