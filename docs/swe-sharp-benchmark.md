# Real C# repository benchmarks

Forge can generate patches for Microsoft's [SWE-Sharp-Bench](https://github.com/microsoft/prose/tree/main/misc/SWE-Sharp-Bench)
and submit them to its official evaluator. The dataset contains 150 tasks from
17 C# repositories. This integration pins PROSE revision
`50cc38f602fffe14073953cf128825ea8d92b188`; downloaded files are checked against
Git blob IDs and recorded with SHA-256 hashes. No solutions or dataset copies are
committed to Forge.

The integration uses the official evaluator directly. Harbor is not currently a
dependency. Existing Forge policy routing, per-model accounting and bounded
rework remain in use. Production application code, configuration, databases,
gates and services are not changed.

## What the score means

There are two separate stages:

1. **Generation:** Forge's engineer, plan critic and final reviewer produce a
   committed patch. The driver reports `generationComplete`/`generationSuccess`.
   `success` remains false and the outcome is `pending-external-evaluation`.
2. **Acceptance:** a trusted supervisor applies that patch in a fresh official
   task container and runs the official hidden tests. Only this stage can report
   `accepted`. A reviewer recommendation alone is insufficient.

The initial selection in `tools/benchmark/swe-sharp-pilot.json` contains eleven
tasks from eleven repositories, selected before model results. Both repair
(`FAIL_TO_PASS`) and regression (`PASS_TO_PASS`) test sets must be nonempty.
Preparation rejects unsupported source layouts and logs every exclusion; it
does not silently substitute easier tasks. The current importer rejects symlinks
and submodules. This pilot permits production `.cs`, documentation `.md`, and
API `.txt` edits; tests, hidden paths, project/build configuration and scripts are
excluded. The restriction is added to the task prompt and checked against the
reference patch before a task is admitted. This is a constrained pilot subset,
not a full SWE-Sharp leaderboard result.

## Prepare on the trusted host (no model calls)

Requirements: Git, Python 3.10+, .NET 10 for Forge, and a working Docker API for
official evaluations. A Podman compatibility socket may be supplied through
`DOCKER_HOST`; compatibility must pass preflight rather than being assumed.
The official task images supply their own repository-specific .NET tooling.
Allow disk space for repository snapshots and container images. The evaluator
uses one container at a time, with 8 GiB RAM, 2 CPUs and 512 PIDs as limits.

From the Forge repository:

```bash
python3 tools/benchmark/swe_sharp.py fetch

uv venv --python 3.13 .portHorizon/benchmarks/swe-sharp/venv
uv pip install --python .portHorizon/benchmarks/swe-sharp/venv/bin/python \
  .portHorizon/benchmarks/swe-sharp/source/50cc38f602fffe14073953cf128825ea8d92b188/harness

python3 tools/benchmark/swe_sharp.py prepare \
  --output .portHorizon/benchmarks/swe-sharp/pilot-01

python3 tools/benchmark/swe_sharp.py preflight \
  --prepared .portHorizon/benchmarks/swe-sharp/pilot-01 \
  --python .portHorizon/benchmarks/swe-sharp/venv/bin/python
```

Fetch/prepare require new output directories and never delete an earlier run.
Preparation downloads only pinned repository base commits. It copies exact Git
blobs into fresh single-commit repositories without original history or remotes.
The harness also sanitizes its clone and verifies that the original history is
physically unavailable. Reference patches, test patches and test lists stay in
the trusted `source/` and `control/` directories. Only `agent/` is an agent input.

Preflight uses the official published `swebcs` images, records their immutable
image IDs, and runs two controls for every admitted task: the reference patch
must resolve it, while the unchanged solution must reproduce a repair-test
failure with all regression tests passing. Because the upstream CLI skips empty
patches, the unchanged-solution control adds an inert marker file, touching no
existing file. Missing/skipped tests, setup errors or an unavailable Docker API
fail preflight. Its receipt is required before the live driver reserves/spends
any model budget. Dependencies, wrapper source and evaluator source are
fingerprinted; image identities must agree across controls and final grading.

## Generate with an isolated agent worker

Build the Release harness normally. In a disposable worker, mount only the
sanitized `agent/` directory and passing receipt at the same absolute paths,
plus Forge's benchmark binaries, tooling and required runtimes. Do not mount the
trusted dataset, reference patches, evaluator environment, Docker socket,
production state, host home or credentials. Supply only the selected model keys
and restrict outbound access to the configured model endpoints. Preserve the
prepared repository paths and contents: changing them invalidates the receipt.
Repository-specific SDKs/dependency caches needed by the engineering agent must
also be provisioned in that worker; grader readiness does not prove agent-worker
readiness.

After filling in a mixed-policy config as described in [benchmark.md](benchmark.md):

```bash
python3 tools/benchmark/run.py --mode live --allow-live --no-build \
  --external-cases /absolute/path/pilot-01/agent/manifest.json \
  --external-preflight /absolute/path/passing-receipt.json \
  --config /absolute/path/policies.local.json \
  --budget-usd 12 --parallel 1 --timeout-seconds 1800
```

The budget is an illustrative reservation cap, not a price forecast. Real
repository tasks may require substantially more context/time than the small
fixtures. Calibrate a few trials before expanding the matrix. Model transport
limitations from the existing benchmark still apply. No live fallback or
automatic purchase occurs. A failed preflight cannot be bypassed through this
driver by changing a mode label.

Fake mode with `--external-cases` exercises patch generation without credentials
or a preflight, but produces only deterministic comment edits. It must never be
treated as a code-quality result. The deterministic external self-test covers
multi-file patches, no changes, reviewer rejection, multiline prompts, and
absence of original history:

```bash
dotnet tools/e2e-harness/bin/Release/net10.0/ph-e2e-harness.dll \
  --benchmark-self-test-external
python3 -m unittest discover -s tools/benchmark -v
```

## Grade exported patches on the trusted host

Return generation artifacts from the worker, keeping their recorded paths
available to the host. The model keys are not needed here:

```bash
python3 tools/benchmark/swe_sharp.py evaluate \
  --prepared .portHorizon/benchmarks/swe-sharp/pilot-01 \
  --results /absolute/path/generation/results.json \
  --preflight /absolute/path/passing-receipt.json \
  --python .portHorizon/benchmarks/swe-sharp/venv/bin/python
```

The supervisor verifies dataset/receipt/base/patch identities and rejects edits
overlapping hidden tests before invoking the official evaluator. Every expected
test must appear in parsed output and agree with the official per-test report.
Containers have networking disabled, no host mounts, dropped capabilities and
resource limits. Only containers bearing this evaluation's unique label are
cleaned up after completion, timeout or interruption. Image pulls happen through
the trusted Docker daemon. Missing cached dependencies that require network at
test time are environment failures, discovered by the no-cost preflight.

The separate evaluation `results.json` is persisted as each attempt finishes and
keeps generation failures and their costs. Partial matrices remain visibly
incomplete; missing spending information stays unknown. Compare total spending
including failed attempts per independently accepted task, first-attempt success,
rework, escalation and latency on matched tasks/repetitions. Reference USD
estimates are not subscription invoices.

## Limits

The official evaluator parses test-runner output. The scope restrictions,
separate containers and exact test reconciliation reduce false positives but
are not a tamper-proof verifier against deliberately forged runner output.
Container-runtime compatibility and image/dependency availability must be
validated on the actual host. Production scheduler concurrency, shared provider
cooldowns, QA evidence, GitHub merging and restart recovery require separate
Forge replay/fault scenarios. No public benchmark score establishes those
properties.
