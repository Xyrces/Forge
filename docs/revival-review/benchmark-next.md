# Benchmarking immediately after architecture acceptance

The next investment should extend the existing benchmark PR, not create a second
benchmark framework. Architecture installation, real self-deployment/rollback,
and the first controlled Forge task remain the current acceptance gates. No live
benchmark, PR mutation, merge or additional spending was performed in this review.

## Existing unmerged work

GitHub currently lists one open Forge PR: **[draft PR #123 — Benchmark mixed-model
engineering with SWE-Sharp repository tasks](https://github.com/Xyrces/Forge/pull/123)**.
Reviewed head: `527c9fa20ed095a7becea9d3fd319dfe837891a9`, branch
`codex/isolated-benchmark`; base `main` at
`4ac44895c05122ff5705a32b4ceb93e60a6e72e5`. It is unmerged and reported mergeable
against that base; this does not establish compatibility with the new architecture
branch. Its exact-head [CI run 36043108157](https://github.com/Xyrces/Forge/actions/runs/36043108157)
is completed/success. Thirty-four changed files primarily add benchmark tooling,
documentation, harness code and tests; CI and the test project overlap integration
surfaces that must be reconciled when rebasing.

The PR already distinguishes generated patches from independently accepted work.
It pins SWE-Sharp source/data, removes future Git history, separates agent inputs
from reference/test material, requires gold and unchanged-code controls, checks
exact exported patch identities, and retains failed attempts and unknown costs.
The pilot selects eleven tasks before observing model outcomes; nine snapshots
were prepared after explicit source-layout exclusions. Only one task had a
validated local evaluator image in the recorded calibration.
[Protocol](https://github.com/Xyrces/Forge/blob/527c9fa20ed095a7becea9d3fd319dfe837891a9/docs/swe-sharp-benchmark.md).

Recorded calibration: **0/2 policies accepted on one task**, one repetition each.
Kimi engineering plus MiniMax critique produced a reviewed patch that failed
independent functional-test compilation; MiniMax engineering exhausted its call
cap. All 36 calls had usage accounting, totaling **$0.3043992 in reference token
cost**, not an invoice. These are useful failure observations, not a model ranking.
[Calibration and diagnostic findings](https://github.com/Xyrces/Forge/blob/527c9fa20ed095a7becea9d3fd319dfe837891a9/docs/benchmark-calibration-2026-09-24.md).

## Compatibility work before paid comparisons

| Existing design | Required adaptation to the new architecture |
|---|---|
| Benchmark factory explicitly uses Chat Completions with transport retries disabled | Add and contract-test the actual Responses path, reasoning settings, output caps, tool round trips, streaming terminal usage and cancellation. Changing a model name is insufficient. Keep at most one outbound call per accounting entry. |
| Disposable SQLite state and fake GitHub exercise engineering flow | Keep this inexpensive component benchmark; label it clearly. Add a separate architecture track that exercises Azure SQL, real Kubernetes worker handoff, worker verification receipts, controller replacement and actual GitHub checks. Never present local fake results as production reliability. |
| Official evaluator needs a Docker-compatible API, validated with rootless Podman | K3s/containerd does not provide that API. Retain the trusted evaluator on a separately provisioned rootless Podman host/process initially, or explicitly port and revalidate it. Do not mount a host runtime socket into an agent or Forge controller. |
| Isolated generation worker receives sanitized repository inputs and model keys | Use dedicated benchmark Jobs/namespace and separate storage/accounting. Do not reuse the production shared PVC, SQL credentials, GitHub credentials or controller service account. A namespace alone is not hostile-code isolation. |
| Per-trial conservative reservations and local atomic usage ledger | Preserve trial/run attribution and unknown liabilities. Reconcile against the new durable budget policy using a separate benchmark dataset/limit; do not charge experimental runs invisibly to the operational pilot allowance. |
| Trusted evaluator verifies offline limits and tests | Re-run controls on the exact image/runtime used for each comparison; preserve network denial, capability/process/memory limits, hidden-input separation and patch hashes. Add disk-space limits/quotas before expanding untrusted workloads. |

Code grounding: [Chat Completions/no-retry factory and metering](https://github.com/Xyrces/Forge/blob/527c9fa20ed095a7becea9d3fd319dfe837891a9/tools/e2e-harness/BenchmarkMetering.cs#L33),
[harness runner construction](https://github.com/Xyrces/Forge/blob/527c9fa20ed095a7becea9d3fd319dfe837891a9/tools/e2e-harness/BenchmarkHarness.cs#L434),
[offline evaluator restrictions](https://github.com/Xyrces/Forge/blob/527c9fa20ed095a7becea9d3fd319dfe837891a9/tools/benchmark/swe_sharp_eval.py#L312),
[scope and accounting limits](https://github.com/Xyrces/Forge/blob/527c9fa20ed095a7becea9d3fd319dfe837891a9/docs/benchmark.md).

## Next-stage acceptance and measurements

1. **Integrate without spending.** Rebase/review PR #123 against the accepted
   architecture, preserving strict graders and all negative controls. Require its
   Python suite, deterministic harness self-tests, and complete reliability test
   discovery to pass. Replay the documented post-PR 429/retry and partial transcript
   failure cases against the new runtime; the old observations are not proof they
   remain broken or are fixed.
2. **Qualify the new transport and execution boundary.** Use offline wire fixtures
   first. Require correct Responses tool/usage mapping, every outbound call backed
   by a reservation, cancellation retaining unknown liability, and no production
   mounts/credentials in benchmark workers. Run gold and repeated unchanged controls
   on every admitted task image before any paid generation.
3. **Run a small, predeclared matched comparison.** Start with one full-capability
   OpenAI policy, then compare a simplified policy that removes only optional
   critique/review orchestration while preserving hard acceptance/security gates.
   Hold tasks, prompts/tools, image, output limits and total per-task budget fixed;
   randomize order and use repeated trials. Expand validated task diversity before
   claiming a routing winner. Record exclusions, infrastructure failures, exhausted
   budgets and unrun cases; never silently replace failures with successful reruns.
4. **Report quality first.** Primary: independently accepted tasks/attempts,
   first-attempt success and regression-test completeness. Secondary: total known
   cost per accepted task including failures; unknown liabilities separately;
   completion latency and queue/build/grading time; rework/escalation counts; tool
   and model calls; critic/reviewer false approvals and false rejections. Report
   confidence intervals and sample sizes; zero accepted tasks means no finite
   cost-per-success claim. Small samples cannot support useful tail-latency claims.
5. **Measure architecture independently.** Record duplicate publication/acceptance
   count, stale-epoch rejection, surviving worker identity during controller
   replacement, recovery time, reservation/usage reconciliation and real current-head
   CI/merge success. Require zero duplicate accepted effects and no unexplained
   paid calls. These tests complement SWE-Sharp correctness; neither replaces the
   other. TimeLoop art, dialogue, design and gameplay need later domain-specific
   acceptance suites and cannot inherit a C# repair benchmark score.

This stage deserves dedicated engineering and a separately approved experiment
budget once the new runtime is demonstrably working. First spend effort on task
environment coverage and trustworthy measurements; parallelize independent trials
only after accounting and isolation pass. No fixed dollar allocation or provider
pricing is assumed here.

## Separate future identity work

After cutover, evaluate GitHub Apps versus dedicated bot accounts for Forge's
worker and approver identities. Define distinct write/review permissions and an
auditable relationship between each action, agent run and responsible identity.
This is a future design item explicitly outside the current task; no account,
application, permission or review-rule changes have been made or researched here.
