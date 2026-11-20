# P4 E2E harness — `tools/e2e-harness`

An end-to-end smoke test for engineering dispatch, PR creation,
review, and task completion against a fresh local repository.
The default fake-agent mode uses a local GitHub service and makes
no model calls. For build prerequisites, start with
[Getting started](getting-started.md#build-and-test-the-code).

## What it verifies

The default fake-runner mode performs these steps.

1. Creates a bare `remote.git`, a local clone, and a `MyApp.csproj` scaffold.
2. Wires the orchestrator to `LocalGitHubService` and a fake agent runner.
3. Creates and approves a synthetic initiative through the stores, binds a
   groomed task to its accepted outcome, and commits a sprint through
   `SprintProposeService`.
4. Calls `OrchestratorAgent.DispatchSingleTaskAsync` for that sprint task.
5. Checks that the local service recorded a PR and that the original task
   carries its `prNumber` metadata.
6. Reads the task worktree and checks for `Calculator.cs`, `CalculatorTests.cs`,
   the `Add` method, and the expected regression-test name.
7. Marks the head's simulated CI green and supplies an approved review override.
8. Calls `PRWatcher.ProcessWatchedTaskAsync` on the original task.
9. Verifies the simulated merge, the task's Completed status, and recorded merge
   evidence matching the task, PR, and accepted head SHA.

The harness scope is **closed-loop**: from task dispatch through
PR opened + merged + task Completed. CI verifies the wiring; the
`--real-llm` flag (off by default) swaps in the real LLM to
exercise the model-driven path.

## What it does NOT verify

- The Intake conversation or approval UI. The harness supplies synthetic
  approval directly through the stores. Intake has separate endpoint and
  browser checks.
- The Designer / Artist / Groomer schedulers. The harness
  bypasses them by passing a pre-built task directly to
  `DispatchSingleTaskAsync`. They're covered by their own
  integration tests.
- Stage B (DTS). Requires Docker / Podman; covered by the
  `deploy/docker-compose.yml` flow + the live verify.
- Real GitHub branch protections, Kubernetes worker isolation, deployment,
  or browser behavior. These need their own integration and acceptance checks.

## Run it

```bash
# From the repo root.
dotnet run --project tools/e2e-harness -- \
    --repo-root="$(pwd)"
```

Expected output (last lines):

```
  PR #2: title="[task] Add Calculator with Add method + xUnit test"
  PR #2: head=agent/task-1 base=main
  diff:
 Calculator.cs      |  6 ++++++
 CalculatorTests.cs | 11 +++++++++++
 2 files changed, 17 insertions(+)

  PASS: PR #2 contains Calculator.cs + CalculatorTests.cs + closed loop merged + tasks Completed.
```

Exit code is 0 on pass, 1 on fail. CI can pipe stdout / stderr
to a log aggregator.

## LLM-free by default (fast, deterministic)

The harness's `FakeAgentRunner` writes the expected files
into the worktree when the workflow's RunAgentExecutor invokes
it. This bypasses model calls and keeps the test fast
(seconds) and deterministic.

## LLM-driven mode (slow, real cost)

Pass `--real-llm` to swap in a real `MafAgentRunner` pointed
at the kilo gateway. The agent is asked the same spec the
fake runner receives; the assertions are unchanged.

```bash
LLM_API_KEY=msy_... \
LLM_BASE_URL=https://api.kilo.ai/api/gateway \
LLM_MODEL=minimax/minimax-m3 \
dotnet run --project tools/e2e-harness -- --real-llm \
    --repo-root=$(pwd)
```

Live-verified on this host: the agent wrote `Calculator.cs`
(6 lines) + `CalculatorTests.cs` (12 lines), committed + pushed
+ opened PR #2; harness marked CI green + approved; PRWatcher
merged; tasks Completed. The PR diff may include spurious
binaries (the agent runs `dotnet test` which restores NuGet
packages) — the assertions still pass because they only check
for the two expected files + symbols.

Cost: ~$0.50-2 in M3 tokens per run (model-driven prompt +
bash tool calls). Use sparingly; prefer the fake runner for
CI.

## Local bare git + `LocalGitHubService`

`LocalGitHubService` is a `GitHubService` subclass that records
PRs in-process via `LocalPrStore`. The orchestrator's `Octokit`
calls are intercepted by the subclass overrides:

- `CreateBranchAsync` / `CreatePullRequestAsync` —
  blocks until `LocalGitHubService.RegisterPushedBranch` is
  called by `PushBridge` (a small thread that tails the bare
  repo's `refs/heads/` and records the SHA).
- `GetPullRequestAsync` — returns the recorded PR.
- `GetCommitStatusAsync` returns the CI state recorded in the local PR store.
- `GetReviewsAsync` returns an empty list. The harness passes an approved
  review override when it drives the watcher.
- `TryMergePullRequestAsync` checks the expected head and records a simulated
  merge. The harness asserts this observation and the resulting task completion.

`GitHubOptions.Mode = "Local"` switches `Program.cs`'s
`BuildGitHubService` to construct `LocalGitHubService`
instead of `GitHubService`. Production callers leave
`Mode = "Remote"` (default).

## Add a new spec

1. Edit `tools/e2e-harness/Program.cs`:
   - The `specBody` variable in `Main`
   - The files `FakeAgentRunner` writes
2. Add assertions below.
3. Rerun.

Keep the test fast (the agent-runner step is fake) and
deterministic (no LLM).

## Known gotchas

- **`UnauthorizedAccessException` on cleanup.** Git's worktree
  metadata can leave locked files on Windows. The harness
  runs `git worktree remove --force` before deleting the
  workspace; this clears the lock. If you re-run quickly and
  see the error, run `git worktree remove --force .` manually
  from the e2e clone + retry.
- **`git` not on PATH.** The harness shells out to `git`
  repeatedly. On Windows, the Visual Studio + Windows SDK
  install adds `C:\Program Files\Git\cmd` to PATH; otherwise
  install Git for Windows or set the env var explicitly.
- **Process.Start cwd validation.** `Process.Start` pre-checks
  the cwd exists. The harness creates the bare / clone dirs
  before invoking `git init` (which would create them but
  too late for `Process.Start`'s validation).

## Where to plug this in CI

The repository's [CI workflow](../.github/workflows/ci.yml) already runs the
harness after the build, C# tests, and release protocol tests.

```yaml
- name: E2E harness
  run: dotnet run --project tools/e2e-harness --no-build --configuration Release -- --repo-root="$(pwd)"
```

Total runtime: 3-5 seconds on a developer laptop, dominated by
the `git init` calls + the in-process dispatch.

## See also

- `docs/p4-restart-safety.md` — the P4 plan doc.
- `LocalGitHubService.cs` — the fake-PR store + Octokit-shape
  adapter.
- `tools/e2e-harness/Program.cs` — the harness itself.
