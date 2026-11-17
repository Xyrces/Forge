# Unattended workflow live qualification

## Version 41 release

On 2026-11-17, PR #131 merged as `3ca60c60610e968111247f7387d6e20226b97654`.
The exact packaged source `8628aa65f0a55d63955e3895d51f99675640c822` passed
CI `44138872845`: 1,986 .NET tests, 44 release/installer tests, and the
deterministic engineering-to-review harness. Image workflow `44138884422`
independently built and tested immutable image
`ghcr.io/xyrces/forge@sha256:688720980720a6ace8eb8f3395cfd95a4df1d34d30edf3af1de007f6476ae9e3`.

The first combined harness attempt exposed an incomplete simulated GitHub
merge response. The adapter now returns the accepted head, merge SHA and
merge time; the harness asserts that exact evidence. Production review
requirements were not weakened to make the harness pass.

Normal candidate/approval operation `deploy-138538c4cbb84ec7923c6a5624072754`
ran the coordinated 40→41 upgrade. Its initializer completed, the release
journal committed, and the independent release Job completed in 86 seconds.
The blue controller `df67563f-c04e-4423-ba1a-955b6d100674` became active and
ready at epoch25. Its `/app/source-revision` matched the qualified receipt.
The read-only application check passed at schema41 without model generation.

Both `core` and `proj_forge` advanced to41. Dataset
`f610a39c-50e1-4aab-8d5a-e18e10aeede9`, the $25 authorization, all four
existing stage holds, and full canonical snapshots of the three budget tables
were preserved. The actual worker credential passed 28 permission checks:
SELECT remained available and INSERT/UPDATE/DELETE were denied on the five
completion authority tables, deployment and sprint proposal audit. This is
permission evidence for that identity, not hostile-worker isolation evidence.

The private deployment configuration enabled routine release for Forge on
the new controller start. The mounted configuration and private installer
copy were updated together and checked. Ordinary routine release still
requires exact merged-source CI and immutable image evidence; schema changes
remain coordinated maintenance operations.

The qualified40/41 bridge was the pinned rollback image. This release did
not inject a new rollback failure. The earlier rollback exercises and the
[isolated SQL compatibility rehearsal](completion-sql-qualification.md)
are distinct evidence, not claims of a fault during this successful cutover.

## Live Intake journey

At17:44 UTC the actual dashboard approved proposal
`proposal-998a7718c7bb43a1928a4f9de029344a`, revision1, hash
`5f909698916d48285aebd6439b7c6038af865394f28315583adcfaaae961e21d`.
The normal transaction created `epic-1` and
`spec-bc3ea950baff4860aad06036a6ed4c6b`. Product refinement completed once
at17:45:09. The specification retained artifact-only running revision
authority, compatible API fields, global UI behavior and separate worker
versus operator verification. All four stage holds were released through
the Flow UI; routine deployment was already open.

This live journey then exposed a real repository freshness defect. The
managed clone and its remote-tracking branch remained at `1b733cb`, while
GitHub reported `3ca60c6`. Grooming read the stale clone and first-run
worktree creation used the stale local branch. Design, grooming and sprint
admission were held before grooming or engineering began. The approved
initiative and completed product refinement were retained. A manual sync
would repair this checkout once, but would not establish unattended
correctness; automatic planning and worktree refresh require a system fix.

The live specification also exposed raw Markdown presentation and an
unhelpful "no available actions" explanation for an automatic design wait.
Those observations are UI findings, not successful journey qualification.

Private receipts and browser screenshots are retained under
`.portHorizon/validation/` in the integration worktree. At this checkpoint,
the new pilot has **not** reached grooming, implementation or delivery.
