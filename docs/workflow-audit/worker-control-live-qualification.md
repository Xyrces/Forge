# Worker control boundary: live qualification

Host, account, network, and deployment identifiers in this archived record use example values. Commit hashes and PR references retain their original meaning.

On November 17, 2026, source
`6b8a78796b70a7a283e7b965390949be62534a04` deployed through the normal
release API as operation `deploy-c62bff8fd51142eb88f8fe2908def03e`.
Image workflow `44117314808` produced immutable image
`ghcr.io/xyrces/forge@sha256:abc1dbe29c09e60485875e47f66b8f65f8d872308c98991763fe33f201e570a6`.
The independent release Job completed and its journal committed. Blue became
the active controller at epoch23, instance/pod UID
`00000000-0000-4000-8000-000000000028`, on the unchanged schema40 dataset.
Design, grooming, sprint and merge gates remained held.

An unauthenticated POST to a nonexistent dashboard API route returned401.
The authenticated host helper reached routing and returned404 for the same
route. Through the actual browser UI over the SSH tunnel, **New session**
created the first live Intake session. This verifies that the dashboard's
server-side mutation client remains usable; it does not claim full
Intake-to-deployment qualification or a model response.

## Network test

The reviewed `forge-worker-public-egress` NetworkPolicy was applied separately,
without regenerating or replacing the live Service. Two temporary pods used
the same qualified image, worker service account, nonroot security context,
immutable read-only probe ConfigMap and worker SQL connection reference.
Neither mounted a service-account token, application configuration, shared
state volume or controller credential. The selected pod additionally carried
the actual `app.kubernetes.io/name=forge-worker` label.

Both probes used run identity `b1fb240f3e0c`. The baseline pod UID was
`00000000-0000-4000-8000-000000000029`; the selected pod UID was
`00000000-0000-4000-8000-000000000030`. The live controller retained the same
UID and address throughout both runs.

| Check | Baseline | Worker policy selected |
| --- | --- | --- |
| Dashboard Service `192.0.2.30:8080` | Connected | Blocked |
| Active controller `192.0.2.31:8080` | Connected | Blocked |
| GitHub and OpenAI DNS/TCP443 | Connected | Connected |
| Actual worker SQL, read-only schema query | Version40 | Version40 |
| Node API6443, observation only | Connected | Blocked |
| Node dashboard8080, observation only | Already unreachable | Unreachable |

No HTTP requests to model APIs were made. The first probe invocation lacked
the image's dependency manifest and failed to load the platform-specific SQL
client. Adding `--depsfile /app/Forge.Core.deps.json` to the same read-only
probe command corrected the harness; both full runs then passed. The initial
failure is retained in the evidence, not counted as a pass.

The two exact temporary pods and ConfigMap were deleted afterward and their
absence verified. The production NetworkPolicy remains installed. Private
manifests, hashes, pod identities and results are under the worker-network
checkout's `.portHorizon/validation/worker-network-probe/`.

## Limits

The operator UI is still a trusted private-network surface. Bearer protection
covers API mutations; worker network isolation also blocks access to the
Blazor server circuit. Public egress is permitted. These tests do not establish
general adversarial container isolation, remove shared-volume/database trust,
or prove every possible node/NAT route. The node8080 observation is not policy
proof because no listener was present before enforcement. No paid calls were
needed; the budget remained $1.01 operator-reported and $23.99 available.
