# Running Forge revision: backend contract and worker checks

## Additive UI contract (task-2)

`GET /api/meta/buildinfo` remains project-independent and returns HTTP 200 JSON:

```json
{
  "informationalVersion": "<unchanged assembly informational version>",
  "framework": "<unchanged runtime framework description>",
  "sourceRevision": "0123456789abcdef0123456789abcdef01234567",
  "commitUrl": "https://github.com/Xyrces/Forge/commit/0123456789abcdef0123456789abcdef01234567"
}
```

The first two fields retain their existing names, string types and computation,
including existing fallbacks. The two new fields are nullable strings, using the
same camel-case serialization contract. This sample SHA is synthetic test data,
not a claim about a deployed build.

Only `/app/source-revision` supplies revision identity. Surrounding whitespace
is trimmed; exactly 40 ASCII hexadecimal characters are accepted and their case
is preserved. The commit URL is exactly the fixed Forge GitHub prefix plus that
full SHA. Missing, unreadable, empty, abbreviated, malformed or multi-value
artifacts produce **both** `"sourceRevision": null` and `"commitUrl": null`.
No version, checkout, project, environment or PR-head fallback is used. The
existing operator-evidence revision callback shares this same validated reader.
The endpoint does not require or consult a selected project; project query
parameters do not affect its result.

For the independent client-1 task: consume `sourceRevision` and `commitUrl`
additively in the existing client model. The first seven SHA characters are the
short display; full SHA/link details and `Unavailable` are presentation concerns.
`Unavailable` is never an API revision value. Client implementation and offline
component/state evidence belong to client-1, not this backend report.

## Worker automated evidence

Worker checks on the local task-2 tree (SDK 10.0.404):

```sh
dotnet clean Forge.Core/Forge.Core.csproj --configuration Release --nologo
dotnet build Forge.Core/Forge.Core.csproj --configuration Release --no-restore --nologo
dotnet test tests/Forge.Tests/Forge.Tests.csproj --configuration Release --nologo --filter 'FullyQualifiedName~RunningRevisionReaderTests|FullyQualifiedName~BuildInfoEndpointTests|FullyQualifiedName~CompletionEndpointTests|FullyQualifiedName~DashboardContractTests'
git diff --check
```

- Clean application build, including referenced Forge.UI Razor compilation:
  **succeeded, 0 warnings, 0 errors**. Initial restore/build also succeeded.
- Focused test run: **67 passed, 0 failed, 0 skipped** (reader, HTTP build-info,
  completion endpoint and dashboard contract tests). Test-project compilation
  reported existing warnings in unrelated tests; none in the added reader/HTTP
  tests. This is not a full-suite or component-presentation acceptance claim.
- Reader/HTTP cases cover mixed-case full SHA, trailing newline and surrounding
  whitespace; empty, abbreviated, overlong, nonhex, Unicode, multi-value, embedded
  whitespace, URL/version-like and NUL-prefixed content; deterministic injected
  missing-file/directory, access-denied and I/O failures. Every read is asserted
  to use only `/app/source-revision`. Cancellation is passed through, not hidden
  as a metadata-unavailable result.
- HTTP assertions cover the exact full-SHA URL, both explicit JSON nulls on
  unavailable metadata, HTTP 200, unchanged existing field names/string types
  and computed values, legacy two-field client compatibility, and unchanged
  payload across no-project/Forge/other-project/Forge/no-project query changes.
  The host has no project services and proxy use is disabled on its local client.
- `git diff --check` passed. Initial valid-case tests caught an incorrectly sized
  synthetic test SHA; the fixture was corrected to 40 characters without
  weakening validation, then the final checks above passed.

Only isolated local HTTP and artifact-read tests were used. No GitHub or live
operator dashboard requests, browser work, deployments or release changes were
performed. The controller must still verify/publish this local tree; these
results do not establish current-head CI, deployment or operator acceptance.

## Rework round 1: retained implementation revalidated

The rework request reports a controller timeout during separate verification,
not an implementation finding. Inspection of local history and the retained diff
found all implementation, deterministic tests and the additive UI contract still
present against base `1bf8750`. No production or test logic was changed in this
round; the only additional edit records this revalidation. The existing six-file
delivery remains local for the worker host/controller to verify and publish.

This round reran:

```sh
dotnet build Forge.Core/Forge.Core.csproj --configuration Release --no-restore --nologo
dotnet test tests/Forge.Tests/Forge.Tests.csproj --configuration Release --no-restore --nologo --filter 'FullyQualifiedName~RunningRevisionReaderTests|FullyQualifiedName~BuildInfoEndpointTests|FullyQualifiedName~CompletionEndpointTests|FullyQualifiedName~DashboardContractTests'
git diff --check
```

The application/Razor build passed with **0 warnings and 0 errors**. The focused
tests passed **67/67**, with **0 failed and 0 skipped**; test-project compilation
reported warnings in unchanged tests and the existing package reference. The
full suite was deliberately not repeated in this model session; independent
exact-tree verification remains the worker host's responsibility. These worker
checks do not establish operator visual or running-image/receipt acceptance,
and no browser/live-dashboard access or remote publication was performed.

## Operator qualification: pending, separate from worker checks

The existing pilot evidence report is
[Unattended workflow live qualification](unattended-live-qualification.md), with
private receipts/browser evidence under `.portHorizon/validation/` in the
integration worktree. Collate actual operator observations there. This backend
report neither supplies nor records OperatorObserved acceptance.

After normally approved deployment, the operator must identify the actual
running workload/image digest and qualified build receipt and compare the full
SHA in four places: dashboard details, `/api/meta/buildinfo`, `/app/source-revision`
read **inside that running image**, and the receipt. Verify the exact Forge
commit URL too. Checkout/PR heads, image tags alone, health and passing offline
tests cannot substitute. Desktop/narrow-width readability, labeled keyboard
full-SHA access, link access, route/no-project behavior, project switching and a
safely arranged unavailable-state check remain operator-led, with scenario,
viewport widths and deployed build identified. No operator observations were
performed by this worker and acceptance remains pending. This is a handoff, not
a deployment authorization or a change to release automation.
