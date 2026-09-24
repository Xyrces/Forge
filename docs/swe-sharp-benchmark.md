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

On Linux, a rootless Podman installation can expose the Docker-compatible API:

```bash
systemctl --user start podman.socket
export DOCKER_HOST="unix://${XDG_RUNTIME_DIR}/podman/podman.sock"
```

Set this only in the benchmark shell. The agent worker must never receive the
socket. Container creation settings and live cgroup limits are checked before
candidate execution; unsupported or silently ignored restrictions fail closed.

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

Preflight requests the upstream harness's `swebcs` image names, records their
immutable image IDs, and runs two controls for every admitted task: the reference patch
must resolve it, while the unchanged solution must reproduce a repair-test
failure with all regression tests passing. A compile-time repair has a separate
negative-control classification: the unchanged solution must build before the
hidden tests are applied, then fail with C# compiler diagnostics confined to a
hidden-test source in one project, with no infrastructure errors or other failing
test results. That failure must repeat in a fresh container with the same image,
evaluator and diagnostic signature. Candidate patches never receive this
exception: every declared repair and regression test must run and pass.
Because the upstream CLI skips empty
patches, the unchanged-solution control adds an inert marker file, touching no
existing file. Missing/skipped tests, setup errors or an unavailable Docker API
fail preflight. Its receipt is required before the live driver reserves/spends
any model budget. Dependencies, wrapper source and evaluator source are
fingerprinted; image identities must agree across controls and final grading.
Public receipts expose only the negative-control kind and signature hash;
compiler details and hidden test identities stay in the private control output.

The trusted wrapper activates the pinned harness's fully qualified test-name
filter for C# CSV rows that omit its undocumented `dotnet` marker, preserving
the harness's special full-suite commands. It independently reconciles all TRX
variants: a later passing parameterization or target framework cannot overwrite
an earlier failure. These compatibility corrections are fingerprinted with the
evaluator and do not alter the declared expected tests.

On 24 September 2026, anonymous pulls of all nine selected `swebcs` images were
denied and Docker Hub listed no public repositories in that namespace. The
namespace default is not evidence that published images are available. Locally
built images must preserve the pinned upstream recipes and have recorded build
provenance; they still require both controls before any model spending.

The rootless Podman builder records generated Dockerfiles and setup-script
hashes, parent image IDs and final image IDs under the private benchmark root:

```bash
.portHorizon/benchmarks/swe-sharp/venv/bin/python tools/benchmark/swe_sharp_images.py \
  --dataset .portHorizon/benchmarks/swe-sharp/pilot-01/control/dataset.json \
  --output .portHorizon/benchmarks/swe-sharp/pilot-01/images \
  --instance-id ardalis__cleanarchitecture-546
```

Omit `--instance-id` to build every prepared task. It tags local images with the
names expected by the upstream evaluator, and refuses to overwrite an image
whose provenance does not match. The build normalizes the upstream x86 platform
spelling to `linux/amd64`, qualifies the Ubuntu registry name, and corrects the
C# environment's parent tag as the upstream build helper does. Build commands
retain upstream setup behavior. Each stage pins its inspected parent image ID.
The instance setup also removes all Git history beyond the original base commit,
in the same build layer as the clone, while preserving the base SHA, tree,
upstream setup edits and build outputs. A global lock prevents competing builds.
`--replace-owned-images` explicitly permits replacing this builder's own tags;
old image contents and unrelated tags remain available.

Image builds need network access for
toolchains and packages; acceptance containers remain offline. Installing Docker
packages inside an image does not grant nested Docker or host-socket access.

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

The driver runs the external-harness self-test before external trials. Fixture
trials retain their separate trusted-grader self-test. This lets a repository
worker use its required SDK (for example .NET 9 RC) alongside the .NET 10 runtime
needed by the prebuilt harness, without requiring the unrelated .NET 10 toy
fixtures to compile. External acceptance still requires the passing official
grader preflight receipt and separate candidate evaluation.

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

The current runtime checks do not enforce a separate writable-layer disk quota.
Use a dedicated disposable host or filesystem quota when evaluating untrusted
or adversarial code; memory/process limits do not bound disk consumption.
