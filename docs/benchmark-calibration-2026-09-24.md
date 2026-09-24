# Repository calibration and reliability findings — 24 September 2026

The first real SWE-Sharp calibration rejected both tested policies. This is one
preselected task (`ardalis__cleanarchitecture-546`) and one repetition per policy,
not a model ranking. Production code, state, routing and gates were unchanged.

| Policy | Outcome | Generation time | Reference token cost |
|---|---|---:|---:|
| Kimi k3-256k engineer/reviewer, MiniMax-M3 plan critic | Reviewed patch failed independent functional-test compilation | 231.757 s | $0.2342394 |
| MiniMax-M3 engineer, Kimi k3-256k critic/reviewer/escalation | Engineering exhausted its 20-call cap without a patch | 50.335 s | $0.0701598 |

All 36 outbound calls had complete usage accounting and no provider errors.
Total reference cost was $0.3043992, without cache discounts; this is not an
invoice or subscription charge. Trial reservations were upper bounds, not spend.
The local meter refused further calls at the cap, and stopped escalation.

The Kimi patch passed real plan critique and final review, but removed a
constructor needed by callers. Independent functional-test compilation failed
with CS1729; neither required test ran. Sixteen unrelated tests passed. The same
immutable environment had already passed gold and repeated unchanged-code
controls, and candidate grading reported no environment error. Model approval
was therefore insufficient evidence of correctness.

The MiniMax observation remains a failed trial under its declared cap. Do not
replace it with a successful rerun or silently enlarge its budget. Any further
trial must retain this result and identify its configuration and repetition.

## Confirmed production findings

### High priority: a recorded 429 after PR creation consumes retries

In `Orchestrator/OrchestratorAgent.cs`, the fresh `lastError` path handles a 429
as strike-free only when neither `reachedPr` nor `alreadyTerminal` is true. When
either is true, execution falls through to `ReportLifecycleAsync(RunDied)` and
`HandleFailureAsync`. This contradicts the adjacent comment that the error is
noise after a completed PR.

Disposable tests using the real dispatch entry point and IssueStore reproduced:

- Dispatcher records `PrOpened`, a PR number and a fresh flattened 429: task
  becomes `Pending` and `retryCount=1`.
- Dispatcher records `Completed` and a fresh flattened 429: status remains
  `Completed`, but `retryCount=1` is still written.

The open-PR probe constructs this persisted state directly; it does not recreate
the full producer race. Normal successful PR creation clears `lastError` first.
The dispatch code documents the same state from earlier live incidents, while
the terminal case can arise when a late run records its error after watch merge.

An unnecessary requeue can spend engineering budget and further retries even
though a PR already exists. A fix needs to distinguish an actually completed
current dispatch from an interrupted rework round; merely suppressing every
error on a task with a PR number would hide real unfinished work. Preserve
terminal states and counters, and test fresh/stale errors and rework checkpoints.

The checked-in `OrchestratorRateLimitRegressionTests` cover the working pre-PR
typed and flattened 429 paths. They assert `Pending` without retry, rework or
no-progress strikes. The post-PR defect was reproduced separately; it is not
fixed or hidden behind a skipped test in this change.

### Medium priority: inner-loop failure loses transcript and gate audit

`Agents/MafAgentRunner.cs` appends response messages and persists the plan-gate
record after the outer MAF `agent.RunAsync` returns. That call may contain many
model/tool turns. An exception inside it reaches the failure handler before
those messages or the gate record are captured.

The real MiniMax trial completed 20 engineer calls. A metered Kimi `Reviewer`
call occurred between calls 17 and 18, proving the plan critic ran. Nevertheless,
the failed run persisted only its initial prompt, zero tool calls and no
transcript; the benchmark could not observe a gate verdict. Usage and heartbeat
tracking survived, but the exact tool sequence and critic verdict did not.

Capture bounded partial transcript/tool results and gate decisions as they
occur, with failure-safe persistence. Test an exception after several inner
tool turns and after critique. Avoid logging credentials or unbounded payloads.
This is a diagnostic/liveness risk, not evidence that the agent made no calls.

## Remaining evidence gaps

Cooldowns are in memory and are lost on restart. Unit and local recovery tests
do not constitute a systemd restart rehearsal under account quota pressure.
Same-task live rework and escalation still need representative repeated trials.
The benchmark does not exercise production scheduling, full Reviewer/QA
dispatch, real GitHub CI/merge behavior, or provider-native transports. GPT-6
and MiMo have not been tested.

Nine repository snapshots are prepared, but only this task has a validated local
image. Two original tasks were excluded for unsupported symlinks/submodules.
Serilog and Polly appear easiest to provision next; readiness-based selection
introduces bias and must be reported. Broader policy conclusions require more
tasks, repeated matched configurations and all failed attempts in the cost.

## Reproducibility

See [setup and evaluator protocol](swe-sharp-benchmark.md) and
[benchmark scope](benchmark.md). Run the no-provider reliability bundle with:

```sh
python3 tools/benchmark/run.py --mode reliability
```

Local evidence is retained under `.portHorizon/` (excluded from Git):

- `benchmarks/swe-sharp/calibration-report.md`: detailed results and exact paths.
- `benchmarks/swe-sharp/calibration-20260924/control/preflight-9cca4148d2/receipt.json`:
  passing gold/repeated-negative controls and immutable image identity.
- `benchmarks/swe-sharp/calibration-20260924/control/evaluation-4fc206d05a/results.json`:
  independent final grades and full accounting for both policies.
- `benchmark-live/swe-sharp-calibration-live-20260924/isolated-bf22021918/`:
  worker limits, containment checks, model ledgers, disposable state and patches.
- `benchmarks/reliability-review-20260924/`: temporary defect repro source,
  instructions, TRX evidence and the expanded no-provider baseline.

The SDK-compatibility setup failure preceding these trials made no model calls
and is retained separately. External runs now use the external-harness self-test;
unrelated .NET 10 fixture compilation is not required in a repository's SDK 9
worker. Candidate acceptance still requires the trusted evaluator.
