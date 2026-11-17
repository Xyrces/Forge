# Forge-first request budget qualification

Verified against official documentation on 2026-11-17 UTC. This is the implemented pilot profile, not evidence of a paid API run.

The only qualified model is `gpt-6.1-sol`, through the direct global `https://api.openai.com/v1` Responses endpoint. The adapter pins `service_tier=default`, model identity and output limit. Proxy routing, regional endpoints, unknown models, hosted tools and media inputs are rejected by the pilot budget policy.

The model documentation specifies a 1,050,000-token context, 922,000 maximum input and 128,000 maximum output. Standard prices start at $2/M input and $10/M output; long inputs apply multipliers. The policy deliberately reserves the full context as input, plus requested output, rather than trying to prove a bound from serialized `ChatMessage` bytes. Provider-native encrypted reasoning/history can be absent from that serialization. [Model specification and pricing](https://developers.openai.com/api/docs/models/gpt-6.1-sol.md).

Minimum qualified rates are **$10/M input and $15/M output**. The input ceiling includes the long-context multiplier and the longest documented cache-write rate. Cache writes replace the input rate rather than adding a second input fee. [Prompt caching pricing](https://developers.openai.com/api/docs/guides/prompt-caching.md).

Configuration below these rates or below the full context ceiling fails before reservation or transport. Larger conservative values are permitted. A $12 per-request ceiling and 8,192 requested output tokens produce a $10.62288 reservation; a $25 dataset limit therefore permits at most two such unsettled requests simultaneously.

On reported usage, the ledger settles using conservative qualified rates, **not an exact invoice price**. Unknown/cancelled outcomes retain their full liability until reliable reconciliation. This can stop work with substantial real credit remaining; it must not silently refund uncertain calls. The bound assumes the qualified provider contract and prices remain valid. Model, tier, endpoint or pricing changes require renewed qualification. No API credit or subscription entitlement is inferred from this ledger.
