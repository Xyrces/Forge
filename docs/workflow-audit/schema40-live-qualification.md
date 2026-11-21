# Schema40 supervisor restart and automatic rollback qualification

Host, account, network, and deployment identifiers in this archived record use example values. Commit hashes and PR references retain their original meaning.

The real K3s/Azure SQL installation completed the additive schema39-to40
migration on November 17, 2026. This rehearsal used no paid model calls and
kept design, grooming, sprint assembly and merge gates held.

## Inputs and deliberate failure

The live rollback bridge was source
`a7c25bcd57078aedba44b46d37a94c56feed0efa`, digest
`06deeee51f2da257fc66fdb10f2dde18e169b3b4bbd4cf116a9960ea3e364ec7`.
Its schema39/40 read compatibility had already been qualified independently.

A dedicated, unmerged test branch produced source
`ccdf11c85147a03b79f8a822db17f45754101181`, digest
`09db253a6c2f50f10d02b0918cbfac3256eb7fa4266260f5d691ebbdc02aeab8`,
from successful image workflow `44111464673`. Its only runtime fault was a
test entrypoint: initializer mode waited 60 seconds then executed the unchanged
Forge binary; controller mode exited deliberately. The release Job's explicit
Python command bypassed that entrypoint. This test branch must not be merged
into production.

Normal deployment API operation
`deploy-50027f5af19b4983bb755636adc633d1` selected the schema-upgrade protocol.
No generated Job, initializer, release journal, shared configuration or database
record was manually patched to obtain success.

## Observations

- At 15:19:24 UTC the initializer was running in its fixed pre-DDL wait. Both
  controller deployments had zero replicas; no controller or worker pod was
  present. After validating image, dataset, Job ownership and the wait marker,
  the observer deleted exactly one release supervisor pod, without force.
- At 15:19:55 UTC a replacement supervisor from the same release Job owned the
  release lock while the same initializer was still Running. Initializer Job
  UID `00000000-0000-4000-8000-000000000018` and pod UID
  `00000000-0000-4000-8000-000000000019` were unchanged. No second initializer
  was created. One early supervisor replacement failed ownership acquisition
  while the deleted pod was terminating; bounded Job retry recovered.
- The initializer finished at 15:20:27 UTC with exit0 and a Complete Job. The
  journal advanced to `migrated`; the candidate emitted the deliberate startup
  failure marker and did not become ready.
- After the normal 600-second readiness timeout, the coordinator automatically
  recorded `rolled-back`, removed candidate pods, and activated the exact
  bridge on schema40. At 15:32:49 UTC blue was ready/active, sole lease owner
  `00000000-0000-4000-8000-000000000020`, epoch21. The failed release Job's
  final Failed status is the expected fault-test result, not a successful
  deployment claim.
- Dataset `00000000-0000-4000-8000-000000000003` was unchanged. Budget remained
  $1.01 operator-reported and $23.99 available, with no outstanding or uncertain
  requests. All four workflow gates remained held.

Read-only probes using the actual worker and controller connections separately
verified core40 and proj_forge40. Worker SELECT on all four initiative tables
remained allowed; all 12 worker INSERT/UPDATE/DELETE permissions were denied.
All corresponding controller permissions remained allowed. This checks database
authority only; network/control-route isolation is a separate boundary.

The normal reviewed image then deployed through operation
`deploy-ce0ba57c6b224390b8457242e9fc32fe`: source
`e1b1045715098919df31827e375e3476ead22b10`, digest
`9bd125844f0b43f164fce71f30bee403b36c495c71503d687a4f88b469569b04`.
Observed schema40 selected the ordinary release protocol rather than repeating
initialization. Its journal committed, route switched to green, and controller
`00000000-0000-4000-8000-000000000021` became healthy/active at epoch22.

## Evidence and limits

Private observer source/output, initializer terminal state, rollback journal and
controller readback are under the integration checkout's
`.portHorizon/validation/`. Actual-principal permission receipts are in the
schema-upgrade checkout's `.portHorizon/validation/live-initiative-permissions/`.

This proves supervisor replacement during an independently running initializer,
real additive migration, and automatic candidate-failure rollback onto the
upgraded database. The supervisor loss occurred before DDL began. It does not
claim a crash during DDL, an initializer SQL failure, database downgrade, or a
complete live Intake-to-deployment user journey.
