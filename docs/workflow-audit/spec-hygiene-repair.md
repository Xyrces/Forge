# Explicit refinement repair after design validation

The specification page shows the latest current-version deterministic design findings and links to Designs. “Refine again” is an explicit operator action that may incur another Product request; it does not approve new scope or relax design validation.

POST `/api/specs/{id}/refine-again?project=...` requires exact proposal ID, approved revision/hash, current spec version and latest HygieneFailed design run ID. The owning ingestion receipt must be Completed and the spec ReadyForDesign. Any child issue, child spec, grooming history or active Designer run refuses repair. Only proven co-located primary-project design evidence is supported; another project/database receives no working action.

The serializable transaction shares the initiative lock with Designer startup. It preserves spec versions and design runs, writes diagnostic context under `initiative/hygiene-repair/{spec}/{version}`, and returns the existing spec to Draft and receipt to Pending atomically. A stale or repeated request conflicts. The existing durable refinement queue resumes after restart and provides the saved diagnostics to Product. Successful refinement increments the version, so the prior context is not reused. Ordinary Failed refinement retries are unchanged.

The memory record is retained operational history, not a cryptographically immutable external audit. Existing memory administration remains available. No schema migration or live data repair is included in this source change. SQL concurrency qualification and browser validation are separate release evidence.
