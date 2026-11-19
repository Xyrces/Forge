# Vision browser qualification

This isolated host runs the production Vision page, project-scoped endpoints,
lossless parser, patch validation, conversation agent, and persistence. Only the
model provider and unrelated shell status endpoints are fixtures. It never loads
the deployment configuration, starts schedulers, or makes a paid model request.

On Linux, build `VisionRehearsal.csproj` in Release, then run its output with
`--source /path/to/vision-snapshot.md --state /path/to/new-disposable-directory
--port 5126`. The state directory must not already exist. The two projects and
their SQLite fixture databases are disposable test infrastructure, not a change
to the production Azure SQL deployment.

Run `NODE_PATH=/path/to/node_modules node tools/vision-rehearsal/verify.cjs
/path/to/evidence` with Playwright installed. Set `CHROMIUM_EXECUTABLE_PATH`
to use a system browser instead of Playwright's bundled Chromium.
Override `VISION_URL` if changing the host port.

Registered-project changes persist in the owning database and prompt memory;
the test checks both real git checkouts remain pristine throughout.

The regression intentionally pins the full original Forge vision snapshot to
SHA256 `58aa439003de01daa72840d4d3c68159e3f35c10da802c3f0402ede96f55efde`.
Retrieve the original input with `git show a9c85f4:docs/MASTER_DESIGN.md > /tmp/forge-vision-snapshot.md` on Linux.
It checks reconstruction before edits, conversation without selecting a node,
draft refinement/restoration, explicit apply/discard, actual disk and memory
contents, source revision conflicts, model errors, second-project isolation,
raw editing and large session transport, new root branches, and mobile layout.
The fixture model is deterministic; it qualifies the integration and interaction
contract, not the live model's editorial judgment.
