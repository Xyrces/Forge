# Forge-first request budget qualification

Forge qualifies three text models for the direct global OpenAI Responses endpoint at Standard pricing. Each request pins the model, uses `service_tier=default`, and limits output. The table gives rates in USD per million tokens as verified on November 17, 2026.

| Model | Ordinary input | Cache read | 30-minute write | 12-hour write | Output | Maximum input / output rates used for reservation |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| [GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol) | 2 | 0.10 | 2.50 | 5 | 10 | 10 / 15 |
| [GPT-6.2 Sol](https://developers.openai.com/api/docs/models/gpt-6.2-sol) | 2 | 0.10 | 2.50 | 5 | 10 | 10 / 15 |
| [GPT-6.2 Astra](https://developers.openai.com/api/docs/models/gpt-6.2-astra) | 8 | 0.40 | 10 | 20 | 40 | 40 / 60 |

For requests above 272,000 input tokens, the input and cache rates double and the output rate increases by 50% for the whole request. All three models support up to 922,000 input tokens, 128,000 output tokens and 1,050,000 tokens of combined context. The 6.2 models require a reasoning effort other than `none`. [Prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching).

Before generation, Forge counts the finalized input, including replayed reasoning, and reserves the maximum cost for that input and the allowed output. At 60,000 input and 8,192 allowed output tokens, the reservation is $0.72288 for Sol or $2.89152 for Astra. Each model needs its own configured budget entry covering the full context and maximum rates above. The pilot enforces $12 per request and $25 across the dataset. [Exact input admission](exact-input-admission.md).

The ledger estimates cost from usage categories returned by the API and records the pricing basis. When cache-write details are incomplete it uses conservative rates. Unknown outcomes retain their liability for reconciliation. Historical records keep the pricing basis recorded at settlement. [Budget accuracy audit](../workflow-audit/budget.md).
