# Current-main validation evidence review

Reviewed 2026-11-16 against Forge `4ac44895c05122ff5705a32b4ceb93e60a6e72e5`. This independent review reads the existing Windows build/test/harness receipts and relevant source. It did not rerun tests, change source, change configuration, contact production, or start a service. Linux-host inspection is separate.

## Result

The current source compiled with NuGet audit disabled for diagnosis, but **the normal build is blocked and the test run is not green**. The 13 observed test failures have identifiable Windows/environment causes; they do not demonstrate 13 platform-independent workflow defects. Conversely, 1,509 passes do not establish that the live Linux deployment or the TimeLoop workflow works.

The deterministic engineering harness also failed before opening a local simulated PR. It provides no successful end-to-end receipt for this restart.

## Receipts

All paths below are relative to the current-source snapshot at `C:/Users/jtn50/repos/Forge/.portHorizon/review-sources/Forge-main/`:

| Receipt | Observed result |
|---|---|
| `build-review.log` | Build failed: 1 error, 3 warnings; NU1902 promoted to error for System.Security.Cryptography.Pkcs 9.0.13, reported moderate-severity advisory GHSA-2fmv-rv2c-59x7. |
| `build-diagnostic-review.log` | Diagnostic build completed: 0 errors, 19 warnings. Parent-run diagnostic used `NuGetAudit=false`; this is compilation evidence, not an approved security exception or normal clean build. |
| `test-review.log` | 1,522 executed: 1,509 passed, 13 failed, 0 skipped; reported duration 1 minute 17 seconds. |
| `.portHorizon/review-results/revival-review.trx` | Independently parsed counters agree: total/executed 1,522, passed 1,509, failed 13, notExecuted 0. Start 19:36:17, finish 19:37:36 Eastern on November 16. |
| `e2e-review.log` | FakeAgentRunner, no LLM. Dispatch success=false, halted at AgentCompleted without surfaced error; no PRs opened. |

The TRX is nested **inside the snapshot's** `.portHorizon/review-results`, not the parent workspace's same-named directory.

## Build interpretation

`Forge.Core/Forge.Core.csproj:9` enables warnings-as-errors. The standard restore/build therefore stops on the package audit finding before establishing a normal usable build. The log names the advisory; this report does not independently establish exploitability or a fixed package version. The dependency path and supported update should be assessed, then the normal audit-enabled build rerun in the implementation phase.

The audit-disabled diagnostic separates this dependency-policy blocker from compilation errors. It does not fix the vulnerability, prove deployment safety, or satisfy the repository's clean-build requirement. The diagnostic warning list includes CA1416 for unsupported Windows Unix-file-mode calls in the test fixtures; these correspond directly to two test failures below. Test-project warnings are allowed by repository conventions, but they remain useful portability evidence.

## The 13 failures

### Eight: hard-coded Linux shell cannot start on Windows

Five `RunVerificationTests` fail at process start:

- `FailingCommand_ReportsExitAndOutputTail`
- `MultipleCommands_CollectsAllFailures`
- `NonTestCommandFailure_NoRetry_GateFails`
- `Timeout_ReportsTimedOut`
- `PassingCommand_Ok`

Three `CommitPushPrExecutorTests` reach the same helper and fail:

- `VerificationPass_ProceedsToPush`
- `VerifyFailure_BounceSeedsReworkContext`
- `VerificationFailure_RequeuesWithOutput_NoPush`

Each stack reports `Win32Exception`, attempting to launch `/bin/bash`, file not found. Source is explicit: `AgentTools/RunVerification.cs:132` sets `/bin/bash`; `:143` starts it. The executor invokes this verification at `Orchestrator/Workflow/CommitPushPrExecutor.cs:262`.

**Classification:** confirmed production portability limitation on this Windows host, not evidence that the verifier's commands fail on the intended Linux host. None of these executions reached the intended command/result assertions, including the timeout test. The implementation currently requires an actual Linux shell at that path; Git for Windows being installed is not sufficient. If only Linux orchestration is supported, document and exercise that contract on Linux; if Windows is intended to work, provide a tested platform-aware process adapter. Do not mark these tests passed or silently skip their semantics.

### Two: Unix permission API in test setup

- `RunVerificationTests.FlakyTests_PassInIsolation_GatePassesWithNote`
- `RunVerificationTests.RealFailures_StillFailInIsolation_GateFails`

Both throw `PlatformNotSupportedException: Unix file modes are not supported on this platform` from `File.SetUnixFileMode` (`tests/Forge.Tests/RunVerificationTests.cs:118` and `:146`). This happens while making a fake dotnet script executable, before the verification policy is exercised.

**Classification:** confirmed Linux-specific test-fixture setup. This run neither validates nor disproves the isolation-retry behavior. The source review separately identifies that policy's causal-inference problem: an introduced order/shared-state bug can pass in isolation. A successful Linux execution of the existing tests would prove the implementation follows that permissive policy, not that the policy is sound.

### Two: fixture cleanup fails before worktree-reuse assertions

- `WorktreeExecutorTests.ReusedWorktree_NoLocalCommits_SyncedToOriginMain`
- `WorktreeExecutorTests.ReusedWorktree_WithLocalCommits_LeftAlone`

Both throw `UnauthorizedAccessException` at `Directory.Delete(tmp, recursive: true)` in `AdvanceOriginMain` (`tests/Forge.Tests/WorktreeExecutorTests.cs:149`). The failing filename looks like a Git object. The helper first clones, commits, and pushes the updated base; deletion then fails before the redispatch/reuse assertions (`:159` and `:184`).

**Classification:** confirmed Windows fixture cleanup/access failure; read-only Git-object attributes or an external file lock are plausible explanations, but the retained log does not distinguish them. This is not evidence that worktree refresh clobbers local commits or fails to sync; those target operations were not reached after the helper returned. Fix cleanup handling within the intended test directories and rerun the relevant behavior on the supported host. Do not use this failure as authorization to recursively delete arbitrary worktrees.

### One: deployment checkout fails because the nested Git path is too large

`DeploymentBuildRunnerTests.RunAsync_PassingCommands_ChecksOutCommitAndMarksBuildPassed` expected BuildPassed but got BuildFailed at `tests/Forge.Tests/DeploymentBuildRunnerTests.cs:93`. Its test commands are already platform-aware (`:73–74`), so attributing this failure to `/bin/bash` would be incorrect.

The console/TRX assertion omits the persisted build detail. Read-only SQLite inspection of the retained test outputs resolves it. In:

`.test-temp/run-24420-2873d44f45e6443eacdf40870e37f433/buildrun-db-52ecffa5e1ef4d548356b6c8ea2fccaa/issues.db`

the candidate's build log records `git worktree add --detach` under the deeply nested review/test/deploy-checkouts path, followed by:

```text
Preparing worktree (detached HEAD 8f776e3)
fatal: '$GIT_DIR' too big
```

The other retained failed candidate in `buildrun-db-d2d0dac69ae54f3e8567d5f358a55f9d/issues.db` records the same early Git error. These databases were opened read-only; no test/state writes were performed.

`DeploymentBuildRunner.cs:54–63` records BuildFailed when worktree creation fails; build/test commands would run later at `:67–79`.

**Classification:** confirmed Git checkout/path-length failure in this deeply nested Windows review location, before build or test commands. It does not establish Linux deployment failure. A shorter disposable test root and supported-host run are appropriate future checks. The expected-failing deployment test can pass because the setup fails first: its pass in this run does **not** prove that configured failing build commands were executed and classified correctly. Tighten such tests to assert the intended stage and exit evidence.

## Deterministic end-to-end harness

The harness prints:

```text
runner = FakeAgentRunner (deterministic, no LLM)
dispatch result: success=False message=workflow halted mid-pipeline at checkpoint AgentCompleted without surfacing an error
FAIL: no PRs were opened
```

Source context:

- `tools/e2e-harness/Program.cs:495` creates `MyApp.csproj`, making default verification applicable.
- The workflow constructors (`:255`, `:277`) do not provide explicit verification commands or detailed executor logging.
- The fake runner (`:599–627`) writes Calculator source/test files and returns success without invoking an LLM.
- `RunVerification.DefaultCommands` (`AgentTools/RunVerification.cs:185`) detects the csproj and returns dotnet build/test; those commands use the Linux-only shell.
- The outer dispatcher detects an unsuccessful mid-pipeline halt and returns the reported error (`Orchestrator/OrchestratorAgent.cs:661`).

**Strong inference, not an observed inner exception:** this failure is consistent with the same missing `/bin/bash` problem seen directly in eight tests: the fake agent finishes, then verification fails to launch before push/PR. The harness's short log and NullLogger wiring do not expose the underlying exception, so it must not be presented as conclusively diagnosed. A future supported-host harness run should retain executor logs and fail with the specific underlying stage error.

The harness also bypasses intake/design/art/grooming and calls `DispatchSingleTaskAsync` directly (`tools/e2e-harness/Program.cs:290–317`). Even a successful future run would establish the deterministic engineering route, not a complete TimeLoop creative/gameplay pipeline, real OpenAI tool calling, production GitHub permissions, actual GPU access, or integrated milestone acceptance.

## Operational conclusions and next verification plan

1. **Normal build remains blocked.** Resolve the audited dependency through an appropriate supported version/path; retain normal security audit and warnings policy.
2. **Supported-host validation remains required.** The Windows run is valuable evidence but cannot certify the proposed Linux service. Run the same current revision in a short isolated Linux test root, preserving full logs and the TRX.
3. **Improve failure observability before relying on unattended execution.** The outer halt guard correctly refuses success, but a generic AgentCompleted halt should carry the original verifier/side-effect exception. Tests should assert the intended failure stage, not only a final Failed status.
4. **Do not count skipped platform work as verified functionality.** Portability fixes or explicitly scoped Linux tests should preserve all command timeout, output, failure bounce, worktree reuse, and deployment-stage checks.
5. **Treat policy and composition findings separately from test counts.** The existing suite can pass while a wrong acceptance rule or missing composition dependency remains. Add targeted tests for the source-review findings, especially staged grooming readiness, project-specific verification, current-head merge, cancelled CI, recovery verification, and art routing.
6. **TimeLoop needs an additional acceptance run.** A fake arithmetic PR is a useful wiring smoke test. It is not a substitute for project skill grounding, required CI checks, image inspection, native gameplay evidence, and final integrated-source qualification.

These are proposed implementation-phase checks, not actions performed by this reviewer. No readiness claim should use the phrase "all tests pass" or "end-to-end verified" on the evidence currently available.
