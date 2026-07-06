# Headroom benchmark — v2 (post-P5.3 spec body split)

Re-measured after P5.3 (spec body split on commit) so the
operator can see whether the smaller spec bodies flowing
through the agent loop shift Headroom's compression wins.

Same harness scope as v1: Calculator scaffold task, single
dispatch cycle, `minimax/minimax-m3` model, varying only
the `--headroom` flag. Headroom sidecar:
`ghcr.io/chopratejas/headroom:latest`, mode `token`, CCR
enabled.

## Headroom-v2 finding: harness had a Windows pipe-buffer deadlock

While running v2 I discovered a real bug in the harness that
was masking the v1 baseline as "incomplete":

- `tools/e2e-harness/Program.cs` `Git.Run` and `Git.Capture`
  called `Process.WaitForExit()` BEFORE `ReadToEnd()`.
- On Windows, when the child writes more than the ~4KB pipe
  buffer (which `git diff --stat` does once the model commits
  any `bin/` or `obj/` artifacts — easy with the real LLM),
  the child blocks on write, the parent blocks on wait,
  neither progresses → silent deadlock.
- v1's `output_shaping` warnings were probably the LLM
  committing `bin/` and the harness hanging on the diff —
  which the operator read as "P5.3 broke the harness".

Fix: read stdout/stderr FIRST, then `WaitForExit`. The
harness is now robust to large diffs (the h5 run below
processed a 109-file diff with no hang).

## Result

Single-run datapoints — see "Why one run is not enough"
below.

| Metric | **No Headroom (baseline)** | **Headroom (token mode)** |
|---|---|---|
| Run timestamp | 2026-07-05 21:50 | 2026-07-05 21:42 |
| LLM calls | 6 | 41 |
| Input tokens (billed) | 6,764 | 494,667 |
| Output tokens | 337 | 5,533 |
| PR contents | `Calculator.cs` (1 line) + `CalculatorTests.cs` (1 line) | `Calculator.cs` + `CalculatorTests.cs` + 109 files of `bin/` + `obj/` |
| Headroom `proxy_inbound.by_path['/chat/completions']` | — | 41 |
| Task outcome | PASS | PASS |

**This run is not directly comparable.** The two runs
diverged on commit shape (2 files vs 109 files) and the
Headroom run triggered a much longer tool loop as a
result. The orchestrator-observed 6,764 vs 494,667 input
tokens reflects the call-count difference more than any
Headroom effect.

## Why one run is not enough

- The model is non-deterministic. Run #1 of the baseline
  committed just `Calculator.cs` + `CalculatorTests.cs`
  (2 files). Earlier runs of the same baseline committed
  109 files including `bin/` + `obj/`. The token count
  scales with file count, so the variance from commit
  shape alone exceeds any plausible Headroom effect.
- P5.3's spec body split changed the prompt the model
  sees, but it also changed the artifact markers the
  model wants to emit — which the model may be responding
  to with more or fewer tool calls in ways the orchestrator
  doesn't gate.
- Headroom's pre-compression delta is still invisible due
  to the persistent counter bug (see v1 caveats).
- We need 5 runs each side on a controlled task (a task
  with a `.gitignore` that prevents `bin/` and `obj/` from
  ever being committed) to converge on a confidence
  interval. That's P5.7's follow-up; the harness fix
  above is the prerequisite.

## Caveats — read before trusting these numbers

1. **The Calculator task is small.** 6-41 LLM calls, 7K-500K
   input tokens. Real engineering tasks in Godot-ECS
   codebases will be 5-10x larger. Headroom's savings scale
   with content size + repetition.

2. **Headroom's `/stats` summary counters are still buggy**
   for our request shape. `summary.api_requests` and
   `tokens.saved` returned 0 even though the proxy served
   41 calls. The real counter is
   `proxy_inbound.by_path['/chat/completions']: 41`. The
   orchestrator's CostTracker is the source of truth for
   what was billed.

3. **One run is not enough.** LLM output is non-deterministic
   and the harness is now reliable (deadlock fixed), so we
   can collect N runs without each one hanging. We didn't,
   in this iteration.

4. **We didn't measure latency.** Headroom docs claim
   sub-millisecond overhead for CacheAligner + 1-50ms for
   SmartCrusher. The harness's per-call timer wasn't wired
   in this iteration. Add later.

5. **P5.5's memory extraction added a post-commit LLM call
   per run.** This shows up in the `calls` count for both
   runs. Negligible token cost (~600 max output), but
   visible in the call count.

## Recommendation for next steps

- **Fix the LLM to not commit `bin/` + `obj/`.** The
  Calculator scaffold should ship a `.gitignore` that
  excludes both. Without that, every benchmark run has a
  high probability of polluting the diff. One-line
  follow-up.
- **Run 3-5 times each side** on the post-`.gitignore`
  harness to get a confidence interval.
- **Try `cache` mode** (Headroom's other mode; freezes prior
  turns for provider KV-cache hits). Per the operator's
  experience, this can save more on long-context tasks but
  costs more on short ones. Worth testing both modes.
- **Patch Headroom's pre-compression counter** so the proxy
  can tell us what it would have billed without
  compression. Without this, the orchestrator's
  CostTracker is a *post-compression* number; we don't see
  the un-compressed baseline.

## How to reproduce (post-fix)

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
- `docs/headroom-benchmark.md` (v1, the original measurement).
- `tools/e2e-harness/Program.cs` — the harness + measurement
  code (now with the pipe-buffer fix).