# Forge benchmark

This benchmark measures engineering task acceptance and records a separate,
LLM-free reliability baseline. It runs in new local Git repositories with local
bare remotes, SQLite state and fake GitHub. It does not start or configure the
installed Forge service, read its project registry, or dispatch its queue.

## Run the no-cost baseline

Requirements: Linux/macOS, Python 3.10+, Git, .NET 10, and restored Forge NuGet
dependencies. The private Talaria packages require the same restore access as a
normal Forge build. Build/test IPC must be permitted in the execution environment.

From the repository root:

```bash
dotnet restore Forge.sln
python3 tools/benchmark/run.py
```

The default `--mode all` builds the harness, verifies that every grader accepts
its reference solution and rejects faulty solutions, runs the selected reliability
tests, and executes all three engineering fixtures with deterministic fake agents. It
makes no LLM calls. Package restoration/build prerequisites may require network
access; this is not a claim that the complete build is air-gapped.

Useful variants:

```bash
python3 tools/benchmark/run.py --mode reliability
python3 tools/benchmark/run.py --mode fake --repetitions 2
python3 tools/benchmark/run.py --mode fake --cases normalize --no-build
python3 -m unittest discover -s tools/benchmark -v
```

Each invocation creates a unique child under `.portHorizon/benchmarks/`.
`--output-root` changes the parent, but never reuses or deletes an existing run.
Each attempt has a separate workspace. Benchmark mode refuses an existing e2e
workspace; the historical smoke harness retains its existing behavior.

Outputs include `report.md`, `results.json`, reliability TRX, and individual
attempt logs/results and grader self-test output. The manifest records commit, dirty state, tool/prompt
source hashes, tracked diff hash, harness binary hash, profile settings,
randomization seed, and build mode. `--no-build`
is only for binaries you have already rebuilt; the flag is recorded, but a Git
commit alone does not prove that an old local binary matches it.

Live attempts also retain agent runs and full/partial tool transcripts in
`workspace/.portHorizon/e2e/state/issues.db` (`agent_run`), with the diagnostic
side-channel at `workspace/.portHorizon/e2e/state/logs/agent.log`. These belong to
the disposable attempt, not the installed service. Inspect them when an approved
plan produces no code or PR; a successful provider response alone does not show
why the agent stopped. Treat transcripts as private execution artifacts.

## What is measured

| Case | Independent acceptance checks |
|---|---|
| `calculator` | Integer division, negative operands, zero divisor, overflow |
| `normalize` | Trimming, Unicode whitespace, null input, invariant lowercase |
| `invoice` | Totals, discounts, validation, arithmetic overflow |

These are small compatibility/engineering screening cases, not a representative
sample of an entire Forge project. The code must be committed and remain within
the fixture's permitted files. A trusted grader generated outside the agent's
worktree evaluates behavior; agent-written tests do not determine acceptance.
The grader checks the exact pushed PR commit. The local GitHub stub records
merge success without performing a real Git merge; no merged-tree claim is made.
Acceptance tests are separate from
the task prompt, but this repository is not a secure hidden-test service.

The real engineering dispatch path, worktrees, commit/push, local PR creation,
and watcher completion are exercised. CI and remote GitHub are simulated. Legacy
profiles simulate final review; live policies invoke a read-only model reviewer.
Grooming, production Reviewer/QA dispatchers, production triage decisions,
production scheduling, and actual remote GitHub/Azure SQL behavior are not
measured here. Fake results
are labeled `wiring-only-pass`. They must never be presented as model scores.

The reliability bundle selects existing tests for model cooldowns, account quota,
request concurrency, agent timeouts, plan gates, QA evidence and attempt budgets,
task states, and startup recovery, including real local Git recovery fixtures.
It reports every discovered case; missing classes, failed or skipped cases, and
zero discovered tests fail the baseline. A green baseline does not demonstrate
that every failure from the project review is fixed. In particular, test-level
crash replay is not a full systemd kill/restart rehearsal.

## Opt-in model trials

Run model-generated code only in a disposable worker/container without production
state, credentials, cloud login caches, service sockets, or host mounts. Separate
directories and a filtered environment are not an OS security sandbox. The agent
has a shell, and the grader executes its code. The no-cost fake suite is safe to
run in the ordinary checkout; live trials need this stronger execution boundary.

Copy `tools/benchmark/profiles.example.json` to
`tools/benchmark/profiles.local.json` and replace every placeholder with verified
provider settings and current rates. The local profile is ignored by Git. API
keys are read from named environment variables, never from the JSON profile.

The current benchmark transport supports text/tool Chat Completions through an
OpenAI-compatible endpoint. It deliberately disables transport retries to make
each metered call correspond to at most one outbound request. This is different
from production's provider retry policy. Responses-only models and Anthropic's
native protocol require an additional tested adapter; changing a model name is
not sufficient. No model IDs, prices, or subscription entitlements are assumed.

Then, in that disposable worker, explicitly opt in and choose a budget:

```bash
python3 tools/benchmark/run.py --mode live --allow-live \
  --config tools/benchmark/profiles.local.json \
  --budget-usd 5 --cases calculator --repetitions 1
```

The command only runs if the budget can reserve at least one complete attempt.
The `5` is an example reservation limit, not a predicted trial cost. Set the API
key environment variables through your worker's secret mechanism beforehand.
The benchmark itself never purchases credits or changes provider plans.

### Mixed role policies

For role-routing experiments, a config may use either the legacy top-level
`profiles` array or a policy document with a `policies` array. Each policy has
`id`, `models`, `roles`, and `maxEngineeringAttempts` (an integer from 1
through 3). `models` uses the same profile object shape as
`profiles.example.json`; every declared model must be referenced by at least
one role. The role map accepts `engineer`, `critic`, `reviewer`, and optional
`escalation` model IDs. See
`tools/benchmark/policies.example.json` for a cheap-engineer/frontier-review
placeholder setup. The IDs and zero rates deliberately require replacement: verify model IDs,
endpoint support, and positive reference rates before running. Policy credential
variable names must start with `BENCHMARK_`; sharing a credential variable across
different providers or endpoints is rejected.

`--parallel 1..8` applies only to independent benchmark subprocess trials. It
does not share production slots, cooldowns, or account state. A policy attempt
reserves the sum of all referenced model caps once, covering bounded engineering
rework plus critic, final-review, and escalation calls from those profiles'
budgets. Reservations happen before a batch/wave starts; a provider failure or
unknown usage stops new waves, while already-running trials finish under their
bounded timeout. Fake mode accepts policy configs without credentials and is
useful for testing routing and reservation logic only.

After filling in a policy config, run a bounded comparison in the disposable worker:

```bash
python3 tools/benchmark/run.py --mode live --allow-live \
  --config tools/benchmark/policies.local.json --budget-usd 12 \
  --cases calculator --parallel 2
```

A policy's `maxEngineeringAttempts` bounds the entire same-task rework loop;
model call caps are shared across all those attempts, not renewed on escalation.
The benchmark chooses escalation after a qualifying failed engineering or
review/grader attempt. This is an experimental routing policy, not the production
triage agent's decision process. Provider/accounting errors halt it without fallback.

Run deterministic workflow failure checks without model calls:

```bash
dotnet tools/e2e-harness/bin/Release/net10.0/ph-e2e-harness.dll --benchmark-self-test-policies
```

In live policy mode, final-review verdicts are strict, while the graders remain
authoritative for acceptance. Legacy profiles keep the simulated final-review
path. This extension does not claim QA, production integration, or production
fault-recovery validation. The current transport still supports only tested
OpenAI-compatible text/tool Chat Completions; native Anthropic and Responses
transports remain unsupported and require a separately verified adapter.

Every profile requires maximum calls, maximum input tokens per call, maximum
output tokens per call, and input/output USD rates per million tokens. For input,
supply an upper rate covering uncached and cache-write charges; cache discounts
are deliberately not applied in the driver's conservative estimate. Verify the
provider's usage semantics before treating estimates as comparable costs.

Before starting an attempt, the driver reserves:

```text
maxCalls × (maxInputTokens × inputRate + maxOutputTokens × outputRate) / 1,000,000
```

Reservations are not refunded, even after a timeout or failed request. This
prevents missing usage from financing additional attempts. The meter caps calls
and requested output tokens, conservatively sizes text/tool input before calls,
and records an atomic metadata-only ledger before/after each call. The supervisor
enforces a wall-clock timeout and kills the attempt's process group, including
shell descendants. Media inputs are outside this text-only benchmark's scope.

This is a conservative local reservation, **not a provider-enforced billing
ceiling**. Tokenizer estimates, gateway-side retries, provider metering, and
non-token fees can differ. Use a dedicated provider budget where available.
Raw token/cache counts are retained; missing/partial usage remains unknown.
Provider failures or unknown accounting stop the remaining live matrix rather
than repeatedly spending against an unavailable account. Reports retain both
attempted failures and unrun jobs.

## Compare results responsibly

Jobs are shuffled reproducibly with `--seed` across profiles, cases, and repeats.
Compare complete matrices using the same fixture/tool versions and limits.
The summary shows completion rate alongside estimated spending divided by
verified completions. Failed attempts contribute their known spending; an
unknown attempt cost makes aggregate cost unknown. Zero completions yield no
cost-per-completion score. A budget-truncated or provider-stopped matrix is
incomplete and should not be used to rank profiles.

This first suite screens compatibility and exposes accounting/reliability
failures cheaply. Before choosing a production routing policy, add pinned
reproductions of real Forge rework, multi-file changes, visual QA tasks, and
frontier/cheap role combinations, with repeated trials and uncertainty reported.
Keep infrastructure outages separate from semantic acceptance failures. Expand
the dataset before making claims about frontier-model quality or production
success rates.
