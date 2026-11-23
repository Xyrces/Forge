# Offline dashboard browser rehearsal

Run on Linux. This hosts actual Blazor components against a loopback-only Python
fixture API. It never connects to the live state database, starts workers or
calls a model. Port5123 is the UI and5124 is the fixture API. Use an isolated
source directory; do not replace a production server.

Prerequisites: .NET10 SDK, Python3, Node.js and Playwright with Chromium installed.

`node scripts/browser/environments.spec.mjs /tmp/forge-environments-ui` exercises
environment policy editing, stale-policy rejection, exact-release promotion,
visible failed requests, target selection and desktop/mobile release history.
It uses only synthetic loopback fixtures and never changes a live environment.
From a disposable checkout, `npm install --no-save playwright` and
`npx playwright install chromium` provide the default browser dependency.
Alternatively set `FORGE_BROWSER_NODE_PACKAGE` to the absolute path of an
external package.json whose node_modules contains playwright.
Set `FORGE_BROWSER_EXECUTABLE` to use installed Chromium/Chrome instead of
Playwright's bundled browser.

In separate Linux terminals, from the repository root:

```sh
python3 scripts/browser/rehearsal-api.py
dotnet run --project tools/browser-rehearsal
node scripts/browser/task-review.spec.mjs /tmp/forge-ui-receipts
```

.NET restore uses the repository's configured package sources. Supply package
credentials privately through the established environment, never in fixtures.

Add `--tasks-only` for a task/run regression. The full runner reuses the board
helper. Captures require settled populated content and equal DOM text before
and after capture; .png.txt files preserve the text. receipt.json records checks,
page errors and overview requests. Content in the inner scroll surface is
scrolled into view before screenshots.

The PR150 fixtures contain allowlisted historical task metadata and verified
GitHub comments/source excerpts. Other scenarios are explicitly synthetic.
These results do not authorize merges or establish live deployment acceptance.

`node scripts/browser/nominated-human.spec.mjs /tmp/forge-nomination` checks the
project nomination control and named task/card actions with synthetic fixtures.
It makes no production setting change.

`node scripts/browser/navigation.spec.mjs /tmp/forge-navigation` exercises actual
board/card/task/run entrypoints, browser Back and app breadcrumbs. It checks
mobile lane and project/sprint context, keyboard menu activation and return
focus, inner `.app-main` scroll after hydration, task-list search/filter and two
loaded pages, and separate project histories. Its fixture API supports exact
query-string responses so paging and project selection are not simulated with
one response for every request. `navigation.json` records completed checks and
browser errors. Run separately from the other fixture suites.

`node scripts/browser/sprint-scroll.spec.mjs /tmp/forge-sprint-scroll` checks
desktop and mobile card returns through breadcrumbs and browser Back while
completion evidence loads one second later than the board. It compares the
actual saved departure scroll and the card's viewport position after rendering,
including missing and failed completion responses. `sprint-scroll.json` records
each comparison. It also checks that explicit sprint/lane changes cancel a
pending restore and resume scroll saving. Wheel, keyboard, pointer, touch,
pagination and task navigation during delayed loading must likewise retain
operator control without a late jump or focus change. Run separately from
other fixture suites.

`node scripts/browser/task-bootstrap.spec.mjs /tmp/forge-task-bootstrap` checks
large direct task, Review and History URLs at desktop/mobile sizes. It verifies
that Retry actually reaches the isolated fixture API, full evidence remains
readable, compact task hydration performs one read, and oversized tasks use
normal interactive reads. ASCII and escaping-heavy boundary probes measure
retained startup frames against the unchanged transport limit.

`node scripts/browser/worker-environment.spec.mjs /tmp/forge-worker-environment-ui`
checks project worker definition saves, stale-revision conflicts, approved image
provenance, and task dependency proposal acknowledgement/dismissal. It captures
2560-pixel desktop and 390-pixel mobile layouts using the real Blazor components
and synthetic loopback API. No image is built, task dispatched, or production
configuration changed. Run separately from other fixture suites.
