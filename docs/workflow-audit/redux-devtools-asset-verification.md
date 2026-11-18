# Redux DevTools asset verification

This record covers the Redux DevTools asset and related dashboard behavior only.
It does not repeat [deployment qualification or the pilot acceptance](unattended-live-qualification.md#final-qualification-2026-11-17).
The automated HTTP check and the operator's browser observations are separate
pieces of evidence.

## Run the published Production check

From the repository root on Linux with Git, the .NET 10 SDK and the normal
NuGet restore configuration:

```sh
(
  set -eu
  output=$(mktemp -d)
  trap 'rm -rf "$output"' EXIT
  dotnet publish Forge.Core/Forge.Core.csproj -c Release -o "$output"
  FORGE_ASSET_CHECK_PUBLISH_DIR="$output" \
    dotnet test tests/Forge.Tests/Forge.Tests.csproj -c Release \
    --filter FullyQualifiedName~PublishedDashboardAssetTests
)
```

Use a fresh publish directory outside the checkout; do not point this check at a
`dotnet build` output or an active deployment directory. It starts the published
DLL from that directory with both host environment variables set to `Production`,
an isolated temporary SQLite project, and a loopback listener. It stops the child
on success or failure. Without `FORGE_ASSET_CHECK_PUBLISH_DIR`, the published
smoke test is **skipped**, while its deterministic tests still run.

The check fetches the real HTML for `/sprints` and `/projects/forge/overview`.
No Redux DevTools script reference is valid. If the HTML includes a Redux DevTools
script `src`, the check resolves it as the page would and requires HTTP 200
without a redirect, a JavaScript media type, and a response containing the Fluxor bridge assignment
and extension API reference rather than an HTML fallback. The test does not execute scripts or
check the browser extension, console, or interactive dashboard behavior.

Local regression proof: a Release publish of baseline `db6565a` started in
Production and failed with HTTP 404 for the script referenced by `/sprints`.
The same HTTP check passed on both routes with a synthetic JavaScript fixture
served from that temporary publish directory. The no-reference path passed in
a deterministic test. These are harness checks, not evidence of the UI fix or a
browser extension connection.

## Deployed Linux — Production browser results

**Status: Pending operator capture.** Do not substitute results from the local
HTTP test or the earlier pilot.

- Serving build/revision: Pending
- Capture time (UTC) and browser/version: Pending
- Operator capture reference: Pending

For each row open a **fresh, uncached direct load** in a real browser, not a
client-side navigation. Use a clean browser context, disable the cache before
load, clear the network and console logs, and set the viewport in CSS pixels.
Capture from navigation until the page has loaded and become interactive; use
that window for all three counts. Count HTTP 404s on asset requests (not API or
document responses), error-level console entries, and uncaught page exceptions
or rejections separately. Keep the URL/message for nonzero counts and note
third-party sources rather than discarding them. Use browser tooling that can
separate exceptions from console errors; the same exception may also be logged
in the console.

Look for `reduxDevTools.js` (including
`/_content/Fluxor.Blazor.Web.ReduxDevTools/reduxDevTools.js`) in the network
capture. If absent, record **No** and **N/A** for HTTP and JavaScript. If
requested, record the actual HTTP result, including failed requests or redirects,
and confirm JavaScript from the response's media type and body, not the `.js`
suffix or status alone. Production should not request the script for the
Development-only integration.

| Route | Width | Script requested (yes/no) | HTTP result | JavaScript confirmed (if requested) | Asset 404 count | Console error count | Uncaught page error count |
| --- | ---: | --- | --- | --- | ---: | ---: | ---: |
| `/sprints` | 1280 px | Pending | Pending | Pending | Pending | Pending | Pending |
| `/sprints` | 390 px | Pending | Pending | Pending | Pending | Pending | Pending |
| `/projects/forge/overview` | 1280 px | Pending | Pending | Pending | Pending | Pending | Pending |
| `/projects/forge/overview` | 390 px | Pending | Pending | Pending | Pending | Pending | Pending |

### Read-only behavior on the same deployment

After measuring the fresh loads, perform these interactive checks at both
widths. Record the visible result, not just that a click or request occurred.

| Check | 1280 px | 390 px | What to capture |
| --- | --- | --- | --- |
| Fluxor update | Pending | Pending | Use **Refresh** on the Forge overview and record the resulting in-place store-backed render without a document reload. If another project is available, switching away and back via the top-bar project selector is another visible Fluxor update; record the scope and content change. Do not change live data to force a different count. |
| Project and sprint navigation | Pending | Pending | From Projects open Forge overview, follow its Sprints navigation, and select a sprint; record the resulting route, selected project, and visible sprint. |
| Pilot sprint (read-only) | Pending | Pending | In Forge sprint history select `sprint-7bf2555ec5`; record the past-sprint read-only task board and what rendered. Do not change the sprint or tasks. |
| Existing completion evidence (read-only) | Pending | Pending | Inspect `completion-sprint-7bf2555ec5` in the completion panel and record its displayed state and existing `operator-browser` and `running-image-comparison` evidence. Do not submit, edit, re-record or otherwise modify acceptance or release evidence. |

## Enabled Development extension — separate browser result

**Status: Pending operator capture.** Use a separate Development instance with
Forge's Redux DevTools integration explicitly enabled and a real browser profile
with the Redux DevTools extension installed and enabled. Do not enable this on
the deployed Production instance. Record the Development build/revision, opt-in
setting, browser and extension versions, route/viewport, and capture reference.
Load the page uncached and confirm that the extension connects to the Forge
Fluxor store and shows a real action/state update (for example, from project
Refresh). Report the actual connection/action result and any script HTTP or
interop errors; a successful page load alone is not a pass.

| Build, opt-in setting, browser/extension versions, route/viewport | Extension connection and observed action/state | Script HTTP result or interop errors | Capture reference |
| --- | --- | --- | --- |
| Pending | Pending | Pending | Pending |

Collate actual operator-supplied observations and capture references in this
file when available. Keep unobserved fields pending; write zero only when the
operator measured zero. Do not update the original pilot qualification or its
completion evidence as part of this asset check.
