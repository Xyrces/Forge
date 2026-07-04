# P4 Headroom — LLM cost optimization

This doc covers how to embed [Headroom](https://headroomlabs-ai.github.io/headroom/)
between the orchestrator and the kilo gateway to compress
context and hit provider KV-caches more often. Default mode is
`token`; CCR is on by default.

## What Headroom does

Headroom sits between your app and the LLM provider as a
transparent HTTP proxy. On every request it runs a two-stage
pipeline:

1. **CacheAligner** — extracts dynamic content (timestamps,
   UUIDs, session tokens) from your system prompt and moves
   it to the end. This stabilizes the prefix so provider
   caches (Anthropic `cache_control`, OpenAI prefix caching)
   hit on repeated calls.
2. **SmartCrusher** — analyzes tool output content (JSON,
   code, logs, diffs, HTML) and compresses it using
   statistical methods. The bulk of token savings come from
   here.

Two modes:
- **`token`** (default; recommended for our orchestrator) —
  maximize compression. Best for short per-task agent
  sessions.
- **`cache`** — freeze prior turns for provider KV-cache
  reuse. Better for long conversations.

Plus **CCR (Compress-Cache-Retrieve)**: original compressed
content is stored in a local LRU; the LLM gets a
`headroom_retrieve` tool to ask for the original. Sub-millisecond
retrieval. CCR is on by default.

**Headline savings** (from the upstream README):

| Scenario | Before | After | Savings |
|---|---|---|---|
| Code search (100 results) | 17,765 | 1,408 | 92% |
| SRE incident debugging | 65,694 | 5,118 | 92% |
| Codebase exploration | 78,502 | 41,254 | 47% |
| GitHub issue triage | 54,174 | 14,761 | 73% |

## How it embeds here

The orchestrator's chat client (`OpenAICompatibleChatClientFactory`)
takes an optional `HeadroomProxyBaseUrl`. When set, the
factory rewrites the kilo gateway's BaseUrl to the local
Headroom sidecar (`http://127.0.0.1:8787`); the sidecar
forwards compressed requests to the upstream kilo gateway.

```
┌─────────────────────┐    ┌────────────────────────┐    ┌────────────────────┐
│  MAF agent loop     │───▶│  Headroom proxy        │───▶│  kilo gateway      │
│  (CostTracker wraps │    │  (token mode, CCR on) │    │  (api.kilo.ai/...) │
│   every call)       │    └────────────────────────┘    └────────────────────┘
└─────────────────────┘
```

`CostTracker` (in-process) observes the LLM response's
`UsageDetails` and aggregates the per-call input/output
tokens. The orchestrator's dashboard reads totals via
`GET /api/cost/stats`.

## Run it

```bash
# 1. Bring up the Headroom sidecar (docker compose or podman-compose).
export OPENAI_API_KEY=msy_...        # kilo gateway key, forwarded to the proxy
export KILO_BASE_URL=https://api.kilo.ai/api/gateway
docker compose -f deploy/docker-compose.headroom.yml up -d

# Verify the sidecar.
curl -s http://127.0.0.1:8787/stats | jq .

# 2. In appsettings.json:
#    "headroom": {
#      "enabled": true,
#      "proxyBaseUrl": "http://127.0.0.1:8787",
#      "mode": "token",
#      "ccrEnabled": true,
#      "trackUsage": true
#    }

# 3. Start the orchestrator. The factory rewrites the kilo
# gateway baseUrl; chat calls go through Headroom.
dotnet run --project PortHorizon.Agents

# 4. Watch the cost dashboard.
curl http://127.0.0.1:4097/api/cost/stats | jq .
```

## Configuration reference

| Field | Default | What it does |
|---|---|---|
| `headroom.enabled` | `false` | Master switch. When true, the factory rewrites the LLM baseUrl to `headroom.proxyBaseUrl`. |
| `headroom.proxyBaseUrl` | `http://127.0.0.1:8787` | Where the sidecar is reachable. |
| `headroom.mode` | `token` | `token` or `cache`. `token` maximizes compression; `cache` freezes prior turns for provider KV-cache reuse. |
| `headroom.ccrEnabled` | `true` | When true, the proxy injects `headroom_retrieve` as a tool. Risk: MAF tool-list collision; LLM may call it. Worth observing on the live verify. |
| `headroom.budgetUsd` | `0` | Daily budget cap (USD). The proxy returns 429 when exceeded. Set 0 to disable. |
| `headroom.trackUsage` | `true` | When true, the in-process `CostTracker` aggregates per-call token counts and exposes them at `GET /api/cost/stats`. |

## Cache vs token mode — when to flip

The orchestrator's hot path is the agent-runner for
engineering dispatch: each task = fresh chat session, ~3-15
turns, tool-heavy. Each task is its own session — there's no
"prior turn" carryover between tasks. **`token` mode is the
right default** for that workload.

Within a single task, `CacheAligner` still extracts dynamic
content from the system prompt so provider prefix caching
helps across the 3-15 turns of one task. That's the within-task
cache win; `cache` mode would optimize cross-task byte
stability, which we don't have.

**Flip to `cache` mode** if:
- You run a long-running agent session that survives task boundaries (Stage B Durable's resumption path).
- You observe the within-task prefix already stabilizes well enough that the `cache` mode's added constraint on the newest turn is worth it.

Until then, leave `mode=token`.

## CCR tool — what we observe + risks

CCR injects `headroom_retrieve` as a tool the LLM can call.
When the LLM calls it, the proxy intercepts the call (~1ms
cache lookup) and returns the original content as a tool
result. MAF then continues the agent loop normally.

Risks:
1. **Tool-list collision.** If we ever add an MAF agent tool
   named `headroom_retrieve`, MAF rejects it. None of our
   agents use that name today.
2. **LLM may call CCR on every turn.** The LLM might prefer
   compressed + retrieve over thinking with what's there.
   This adds latency (1ms locally) but no cost. Worth
   observing on the live verify.
3. **Compression round-trip on tight loops.** If the LLM
   compresses + retrieves + re-compresses in the same turn,
   we waste time. Rare; not a real concern.

## Observability

| Endpoint | What it tells you |
|---|---|
| `GET /api/cost/stats` | Per-call input/output tokens from the LLM response (post-compression, i.e. what the provider bills). |
| `POST /api/cost/reset` | Clear counters. |
| `GET http://127.0.0.1:8787/stats` (Headroom's own endpoint) | Pre-compression tokens + cache hit/miss counts. |

The Cost tab on the dashboard (`http://127.0.0.1:4097` →
Cost tab) renders `/api/cost/stats`. For the full picture
(pre-compression delta), also poll the proxy's `/stats` and
diff against the orchestrator's totals.

## Known limitations

- **CCR retrieval latency for very long tool outputs.** A
  100 MB tool output compressed to 1 KB markers + 1 MB
  retrieved = the LLM still pays 1 MB tokens on retrieval.
  Headroom can't help with that beyond the initial
  compression.
- **Provider-specific cache_control / prefix caching.** Only
  Anthropic + OpenAI + Google are supported by Headroom's
  cache backends. We're on kilo gateway which speaks
  OpenAI-compatible, so we get the OpenAI prefix caching.
- **Not a substitute for prompt design.** Headroom compresses
  waste; it doesn't fix "your system prompt is 10 KB of
  instructions." Aim for < 2 KB per agent role.

## See also

- `deploy/docker-compose.headroom.yml` — the sidecar compose.
- `Core/CostTracker.cs` — the in-process aggregator.
- `Agents/OpenAICompatibleChatClientFactory.cs` — the rewrite
  seam.
- `Dashboard/CostEndpoints.cs` — `GET /api/cost/stats`.