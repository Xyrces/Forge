# Headroom benchmark — initial measurement

Single-task harness run, same Calculator spec, same
`minimax/minimax-m3` model, varying only the `--headroom` flag.
Headroom sidecar: `ghcr.io/chopratejas/headroom:latest`, mode
`token`, CCR enabled.

## What was measured

The orchestrator's `CostTracker` observes every LLM response's
`UsageDetails` from the kilo gateway. The number it reports
is what the provider bills. With `--headroom`, the chat
client factory rewrites the kilo baseUrl to the local
Headroom proxy (`http://127.0.0.1:8787`); the proxy
compresses + cache-aligns + forwards to kilo. The orchestrator
sees the post-compression numbers.

Headroom's own `/stats` endpoint reports pre-compression
totals (when its counter works — see Caveats below).

## Result

| Metric | **No Headroom (baseline)** | **Headroom (token mode)** | Δ |
|---|---|---|---|
| LLM calls | 25 | 24 | -1 |
| Input tokens (billed) | **79,676** | **69,256** | **-10,420 (-13.1%)** |
| Output tokens | 3,032 | 2,850 | -182 (-6.0%) |
| Headroom `proxy_inbound.by_path['/chat/completions']` | — | 52 | (proxy served 52 calls) |
| Headroom `proxy_inbound.by_status['200']` | — | 55 | (49 success after compression + 4 stats probes) |
| Task outcome | ✅ PR #2 opened + merged + tasks Completed | ✅ same | parity |

**Net win: -13.1% input tokens.** The orchestrator's
engineering-dispatch workflow is tool-heavy + multi-turn;
each call rebuilds the conversation history which is the
bulk of the input. Headroom's compression reduces the
boilerplate.

## Caveats — read before trusting these numbers

1. **The Calculator task is small.** 24-25 LLM calls, ~80K
   input tokens, ~3K output. Real engineering tasks in
   Godot-ECS codebases will be 5-10x larger. Headroom's
   savings scale with content size + repetition (the same
   file diff read twice is a great cache hit). A small task
   understates the savings.

2. **Headroom's `/stats` summary counters are buggy for our
   request shape.** `summary.api_requests` and
   `tokens.saved` returned 0 even though the proxy actually
   served 52 calls. The real counter is
   `proxy_inbound.by_path['/chat/completions']: 52`. The
   orchestrator's CostTracker is the source of truth for
   what was billed.

3. **One run is not enough.** LLM output is non-deterministic
   (different file contents, different conversation
   branching). The 13.1% delta could be 8% or 18% on a
   different seed. Need 3-5 runs each side to converge on a
   confidence interval.

4. **We didn't measure latency.** Headroom docs claim
   sub-millisecond overhead for CacheAligner + 1-50ms for
   SmartCrusher. The harness's per-call timer wasn't wired
   in this iteration. Add later.

5. **Headroom's pre-compression delta isn't visible** because
   of the counter bug above. To get a clean delta, we need
   to either patch their counter or instrument our own
   pre-compression size (which would require running our own
   compression pipeline locally — defeating the purpose).

## Recommendation for next steps

- **Run again with a bigger task.** Find a representative
  PortHorizon dispatch (the longest recent task in the live
  DB) and replay it via the harness. The 13% should grow
  significantly.
- **Run 3-5 times each side** to get a confidence interval.
- **Try `cache` mode** (Headroom's other mode; freezes prior
  turns for provider KV-cache hits). Per the operator's
  experience, this can save more on long-context tasks but
  costs more on short ones. Worth testing both modes.
- **Switch the role-level LLM model** to one that gets billed
  more aggressively (operator chose not to — current model
  is fine for the workload) and observe the same Headroom
  benefit on a higher baseline.

## How to reproduce

```bash
# 1. Start Headroom.
docker compose -f deploy/docker-compose.headroom.yml up -d

# 2. Set the kilo key.
export LLM_API_KEY=eyJ...
export LLM_BASE_URL=https://api.kilo.ai/api/gateway
export LLM_MODEL=minimax/minimax-m3

# 3. Run the baseline (no Headroom).
rm -rf .portHorizon/e2e
dotnet run --project tools/e2e-harness -- \
    --repo-root=$PWD -- --real-llm 2>&1 | tail -30

# 4. Run with Headroom.
rm -rf .portHorizon/e2e
dotnet run --project tools/e2e-harness -- \
    --repo-root=$PWD -- --real-llm --headroom 2>&1 | tail -30

# 5. Cross-check Headroom's own counters.
curl -s http://127.0.0.1:8787/stats | jq .proxy_inbound
```

## See also

- `docs/headroom.md` — operator guide for Headroom itself.
- `tools/e2e-harness/Program.cs` — the harness + measurement
  code.