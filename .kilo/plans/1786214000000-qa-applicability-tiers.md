# QA stage: 3-tier applicability gate (deterministic diff classification)

## Context (verified live 2026-08-25)

qa-1 ("Define canonical QA-evidence location/policy", porthorizon PR #1041) looped to
`qa-unavailable`: its diff is 100% docs/policy/evidence files (`.gitignore`, `docs/QA/**`,
`test-results/qa/**`) — zero product code — so a raster screenshot of the running client is
unfulfillable. The QA agent correctly passes with prose; the dispatcher discards it ("pass
verdict without raster screenshot evidence — not QA", now visible via `qaLastError` from #114).
Release-50/51 fixes made the failure VISIBLE; this plan removes the structural cause: the raster
bar applies to every head regardless of what the head changes.

## Operator decisions (2026-08-25)

- **3-tier deterministic diff gate.** The dispatcher classifies the head's diff; the agent never
  self-declares applicability.
- **Tier-2 pass requires evidence files** (any type) under `test-results/qa/<taskId>/`, shipped by
  the dispatcher like today. Zero-evidence pass = discarded as not-QA (same teeth as tier 1's
  raster rule).

## Tiers (highest applicable tier wins on a mixed diff)

| Tier | Condition (paths in `git diff --name-only origin/<default>...HEAD`) | QA behavior |
|---|---|---|
| 1 — visual | any path under the project's visual prefixes | Full QA; raster PNG/JPG mandatory (today's bar, unchanged) |
| 2 — code | non-visual, non-docs paths (Core/, tests, scripts, …) | QA runs, must PLAY the sim via the documented harness; pass requires ≥1 new/changed evidence file under `test-results/qa/<taskId>/` (any type); raster NOT demanded |
| 3 — docs | every path is docs/config/evidence | NO agent run, no attempt spent; dispatcher stamps `qaVerdict=not-applicable` at the head |

- Tier-3 path set: `docs/`, `**.md`, `.gitignore`, `.gitattributes`, `LICENSE*`, `test-results/`.
  Anything else ⇒ not docs. (`.github/` workflows are deliberately NOT docs — code tier,
  conservative.)
- Visual prefixes: `roles_json.$qa.visualPaths` (new optional key) → default: the project's
  clientdev `$territory` prefixes (porthorizon: `PortHorizon.Client/`; forge: `Forge.UI/`).
  Empty/unconfigured ⇒ nothing is visual (all code is tier 2) — fail-open toward LESS demand only
  when no visual surface is configured at all.
- Empty diff / unclassifiable ⇒ tier 2 (conservative: QA runs).

## Mechanics

1. **Classifier**: static `QaEvidenceTierClassifier` (Reviewer/, pure function over a path list +
   visual prefixes) → unit-testable. Classification computed per QA attempt in the synced QA
   worktree (cheap local git diff); result stamped as `qaTier` metadata (TaskDetail/audit).
2. **Tier 3 path** (`QaDispatcher.VerifyOnceAsync`, before any run): stamp `qaSha=headSha`,
   `qaForSha=headSha`, `qaVerdict=not-applicable`, `qaNotes="docs-only diff (N files): <first
   few>"`, clear `qaAttempts/qaAttemptSha/qaStartedAt`; publish the DashboardEvent; the
   `WatchSweepService` continuation treats `not-applicable` like `pass` for the review relaunch
   (extend the `t.Result.Verdict == VerdictPass` condition).
3. **Tier 2 path**: run proceeds; `BuildPrompt` gains tier context ("this head touches no visual
   paths — drive the sim and prove behavior with state-assertion evidence under
   `test-results/qa/<taskId>/`; raster not required"); the dispatcher's raster gate is replaced
   by an any-file-under-`test-results/qa/<taskId>/` gate for tier-2 heads; the non-evidence-path
   refusal and dispatcher-ships-the-evidence model are unchanged.
4. **Tier 1**: unchanged.
5. **Merge gate** (`Reviewer/PRWatcher.cs:358`): `qaPassed` accepts `pass` OR `not-applicable`
   when current at head. **Reviewer self-skip** (`ReviewerDispatcher.cs:151`): already satisfied
   by `not-applicable` (verdict non-empty, not fail) — pin with a test, change only if needed.
6. **Prompts/docs**: `agents/qa.md` tier-aware role card; `AGENTS.md` QA-stage paragraph gains
   the 3-tier contract.
7. **Tests**: classifier (tier assignment, mixed diffs → highest wins, empty → 2, unknown → 2,
   visualPaths override); tier-3 (no run, exact metadata stamps, attempts cleared, review
   relaunched); tier-2 (any-file evidence accepted, zero-evidence discarded, raster not
   demanded); tier-1 unchanged; merge gate + reviewer self-skip with `not-applicable` at head.
8. **Deploy** release 54 (operator-authorized; standard runbook — confirm no active runs; no
   gate holds needed; rollback = repoint to 53; no schema change).

## Live proof (post-deploy)

qa-1's next QA evaluation classifies head caf9991 as docs-only → `not-applicable` stamped →
review relaunches → merge gate satisfied → PR #1041 merges with zero further QA runs. Capture:
`qaTier`/`qaVerdict` metadata, the review run following, the merge. Also confirms the open
triage ledger row 11 (qa-1's park) resolves on merge.

## Risks / failure modes

- Misconfigured visual prefixes let a rendering change land without raster QA — mitigated by
  conservative defaults (clientdev territory), the `$qa.visualPaths` override, and auditable
  `qaTier` metadata per verdict.
- Tier-2 laziness (prose-only pass): blocked — evidence files required, dispatcher-shipped.
- `not-applicable` stamps `qaSha == headSha` with no commit — the existing head-currency checks
  (`qaSha == watchHeadSha ?? branchSha`) hold by construction.
- A head that flips tier between attempts (dev pushes docs after code): classification is
  recomputed per attempt — a docs-only head after a code head correctly demotes.

## Out of scope

- Extension-based visual detection (`.tscn`/`.tres` outside the client prefix), `$qa.visualPaths`
  UI, tier-2 evidence-type validation beyond presence, changes to the raster bar itself,
  reviewer contract changes beyond gate acceptance.
