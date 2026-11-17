# Explicit refinement repair after design validation

The specification page shows the latest current-version deterministic design findings and links to Designs. “Refine again” is an explicit operator action that may incur another Product request; it does not approve new scope or relax design validation.

POST `/api/specs/{id}/refine-again?project=...` requires exact proposal ID, approved revision/hash, current spec version and latest HygieneFailed design run ID. The owning ingestion receipt must be Completed and the spec ReadyForDesign. Any child issue, child spec, grooming history or active Designer run refuses repair. Only proven co-located primary-project design evidence is supported; another project/database receives no working action.

The serializable transaction shares the initiative lock with Designer startup. It preserves spec versions and design runs, writes diagnostic context under `initiative/hygiene-repair/{spec}/{version}`, and returns the existing spec to Draft and receipt to Pending atomically. A stale or repeated request conflicts. The existing durable refinement queue resumes after restart and provides the saved diagnostics to Product. Successful refinement increments the version, so the prior context is not reused. Ordinary Failed refinement retries are unchanged.

The memory record is retained operational history, not a cryptographically immutable external audit. Existing memory administration remains available. No schema migration or live data repair is included in this source change. SQL concurrency qualification and browser validation are separate release evidence.

## Qualification

The backend at `55229a96b9dcab7ddee9830a19c80763f2cfe155` passed actual Azure SQL qualification in initially absent Workload schema `audit_refinement_3eb16a89024b41a0a19338e80eacfff0`. Tests covered exact identity/version/latest-run checks, stale and repeated requests, cross-project refusal, durable diagnostics after claim/reconstruction, no stale context after a successful version increment, both Designer admission orderings and three concurrent one-winner attempts, downstream-work refusal, and trigger-injected transaction rollback. The schema was removed and absence verified. Production tables were untouched.

Private harness SHA256: `a9a729b54ec95bcc19c8e4e72db0ce11b5443bede84fc2450611236f9cd16020`; result SHA256: `2cbeeda2896384c35716481f4c1feef0ad057c8182c70b91ffcae3656cbadc9a`. The subsequent `d1b8a6b` change affects shared CSS and banner classes only.

Actual browser checks at 1280×900 and 480×850 found and corrected a compressed horizontal banner. The final layout showed readable findings, cost notice and retry control. Clicking Refine again queued the same approved v2 spec, preserved its body and removed the retry control. Returning to the page retained Pending; another project's scope returned 404. These disposable fixture checks ran without a scheduler or model client. They prove admission and UI behavior, not a completed paid refinement or delivered pilot.

Linux CI `44149165714` at `d1b8a6ba548c1230bc96b7edf53c673b0d94f880` passed all 2,023 .NET tests, 44 deployment checks and the offline engineering/review harness. Image workflow `44149197044` passed. PR #133 merged as `8c29f524bb389178716c562a78bda5e6af9875dd`; live release and pilot evidence are recorded in `unattended-live-qualification.md`.
