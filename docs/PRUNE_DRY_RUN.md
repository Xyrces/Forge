# Branch prune — dry run

Generated for task-35. The actual `git push origin --delete` commands in the
next section are the exact commands that will be issued. None have been
executed at the time this file was committed.

## Source of truth

- `docs/branch-audit.json` (generated at `2026-07-29T12:44:30.5465864Z` by `tools/branch-audit`)
- `docs/BRANCH_AUDIT.md` (human-readable view of the same data)
- Default branch: `main`

## Counts (must reconcile)

- **Total remote branches**: 39
- **Protected** (never eligible for deletion): 1
- **Fully merged into `origin/main` + non-protected → DELETE**: 6
- **Unmerged + non-protected → KEEP**: 32

Reconciliation: 1 + 6 + 32 = 39 (must equal 39)

## Deletion criteria

A branch is deleted only when ALL of the following hold:

1. `merged_into_main == true` — `git merge-base --is-ancestor <sha> origin/main` exits 0.
2. `protected == false` — branch name is not in `{main, master, develop, HEAD}` and not in the configured protection list.
3. Category is `agent/*` (the only stale pattern present; no `polecat/*`, `convoy/*`, `gt*`, `ph-*`, or `POR-*` branches exist on `origin` per the audit).

Unmerged branches are kept regardless of age or category, per the conservative
prune policy. In-flight or stranded work from prior fleets must not be clobbered.

## Commands to be issued (none executed yet)

Each command is a separate `git push origin --delete <branch>` invocation,
one branch at a time. If any single command fails, the remaining deletes
are skipped and the failure is reported in the PR description.

### 1. `git push origin --delete agent/story-1`

- branch: `agent/story-1`
- category: `agent`
- tip_sha: `c1f722d629ea376678113291a4e1219e6e36bcc9` (c1f722d)
- last_commit_date: `2026-07-07T18:31:49-04:00`
- merged_into_main: **True** (`git merge-base --is-ancestor c1f722d origin/main` exits 0)
- protected: **False**

### 2. `git push origin --delete agent/task-188`

- branch: `agent/task-188`
- category: `agent`
- tip_sha: `644ae67ccbc2a928b2aa701cbc0a345de9c6391c` (644ae67)
- last_commit_date: `2026-07-26T14:30:16-04:00`
- merged_into_main: **True** (`git merge-base --is-ancestor 644ae67 origin/main` exits 0)
- protected: **False**

### 3. `git push origin --delete agent/task-202`

- branch: `agent/task-202`
- category: `agent`
- tip_sha: `5564c84f80c92bc2a63c145ecb645d8c5357d983` (5564c84)
- last_commit_date: `2026-07-26T22:18:43-04:00`
- merged_into_main: **True** (`git merge-base --is-ancestor 5564c84 origin/main` exits 0)
- protected: **False**

### 4. `git push origin --delete agent/task-210`

- branch: `agent/task-210`
- category: `agent`
- tip_sha: `456257c631c1e2c8477d2c3dad1d5233a566c96a` (456257c)
- last_commit_date: `2026-07-27T01:44:24-04:00`
- merged_into_main: **True** (`git merge-base --is-ancestor 456257c origin/main` exits 0)
- protected: **False**

### 5. `git push origin --delete agent/task-30`

- branch: `agent/task-30`
- category: `agent`
- tip_sha: `207c254cb644253f1804023d786d6d8769599db8` (207c254)
- last_commit_date: `2026-07-28T20:25:50-04:00`
- merged_into_main: **True** (`git merge-base --is-ancestor 207c254 origin/main` exits 0)
- protected: **False**

### 6. `git push origin --delete agent/task-31`

- branch: `agent/task-31`
- category: `agent`
- tip_sha: `207c254cb644253f1804023d786d6d8769599db8` (207c254)
- last_commit_date: `2026-07-28T20:25:50-04:00`
- merged_into_main: **True** (`git merge-base --is-ancestor 207c254 origin/main` exits 0)
- protected: **False**

## Branches to KEEP (unmerged into `origin/main`)

These 32 branches are NOT deleted in this prune. They remain for operator review:

- `agent/task-11` (category=`agent`, tip=`ca016bb`, last_commit=`2026-07-22T15:59:50-04:00`)
- `agent/task-12` (category=`agent`, tip=`5814682`, last_commit=`2026-07-22T16:04:07-04:00`)
- `agent/task-14` (category=`agent`, tip=`731e7c4`, last_commit=`2026-07-20T14:11:53-04:00`)
- `agent/task-148` (category=`agent`, tip=`9ca0dd9`, last_commit=`2026-07-22T20:31:53-04:00`)
- `agent/task-149` (category=`agent`, tip=`4e234ff`, last_commit=`2026-07-22T20:37:21-04:00`)
- `agent/task-161` (category=`agent`, tip=`c035566`, last_commit=`2026-07-25T19:22:51-04:00`)
- `agent/task-162` (category=`agent`, tip=`1ca3317`, last_commit=`2026-07-26T08:43:03-04:00`)
- `agent/task-164` (category=`agent`, tip=`620057e`, last_commit=`2026-07-26T08:54:08-04:00`)
- `agent/task-2` (category=`agent`, tip=`82ff959`, last_commit=`2026-07-18T08:16:55-04:00`)
- `agent/task-205` (category=`agent`, tip=`4646f27`, last_commit=`2026-07-27T01:52:00-04:00`)
- `agent/task-40` (category=`agent`, tip=`a2835fc`, last_commit=`2026-07-22T16:47:04-04:00`)
- `agent/task-45` (category=`agent`, tip=`c44202c`, last_commit=`2026-07-22T17:10:07-04:00`)
- `agent/task-46` (category=`agent`, tip=`227a0c3`, last_commit=`2026-07-22T17:13:31-04:00`)
- `agent/task-49` (category=`agent`, tip=`ba0c026`, last_commit=`2026-07-22T17:20:25-04:00`)
- `agent/task-50` (category=`agent`, tip=`033e14b`, last_commit=`2026-07-22T17:22:28-04:00`)
- `agent/task-55` (category=`agent`, tip=`e96518d`, last_commit=`2026-07-22T17:35:04-04:00`)
- `agent/task-58` (category=`agent`, tip=`b31d3f5`, last_commit=`2026-07-22T17:41:17-04:00`)
- `agent/task-6` (category=`agent`, tip=`3139e1b`, last_commit=`2026-07-22T13:38:02-04:00`)
- `agent/task-60` (category=`agent`, tip=`9796ba3`, last_commit=`2026-07-22T17:49:08-04:00`)
- `agent/task-64` (category=`agent`, tip=`3a25487`, last_commit=`2026-07-22T18:04:46-04:00`)
- `agent/task-65` (category=`agent`, tip=`2936021`, last_commit=`2026-07-22T18:08:26-04:00`)
- `agent/task-66` (category=`agent`, tip=`51008cb`, last_commit=`2026-07-22T18:10:26-04:00`)
- `agent/task-70` (category=`agent`, tip=`a49b789`, last_commit=`2026-07-22T18:18:01-04:00`)
- `agent/task-72` (category=`agent`, tip=`b4aef14`, last_commit=`2026-07-22T18:31:14-04:00`)
- `agent/task-78` (category=`agent`, tip=`d8618d6`, last_commit=`2026-07-22T18:50:11-04:00`)
- `agent/task-79` (category=`agent`, tip=`96b29d6`, last_commit=`2026-07-22T18:54:42-04:00`)
- `agent/task-84` (category=`agent`, tip=`ddc9e8a`, last_commit=`2026-07-22T19:02:54-04:00`)
- `agent/task-85` (category=`agent`, tip=`703f288`, last_commit=`2026-07-22T19:05:26-04:00`)
- `agent/task-88` (category=`agent`, tip=`d56df57`, last_commit=`2026-07-22T19:13:53-04:00`)
- `agent/task-9` (category=`agent`, tip=`6c6066a`, last_commit=`2026-07-20T10:23:09-04:00`)
- `agent/task-90` (category=`agent`, tip=`4c009fd`, last_commit=`2026-07-22T20:19:35-04:00`)
- `test/push-agent-12` (category=`other`, tip=`fff9df4`, last_commit=`2026-07-20T13:19:32-04:00`)

## Protected branches (never eligible for deletion)

- `main` (tip=`207c254`, protected by `BranchProtector.AlwaysProtected`)

## Out of scope

- No tag deletion.
- No local-branch deletion (this worktree keeps `agent/task-35`; the orchestrator-created worktrees for the deleted branches are cleaned up by `PRWatcher` / `GitWorktreeService.RemoveAsync` after merge, not here).
- No branch-protection changes.
- No history rewrite.
- No `--force` / force-push.

_Dry run captured at `2026-07-29T13:06:15.366128+00:00` by `tools/branch-audit` JSON sidecar + this generator. The audit itself was produced by task-33 and is on `main` (PR #66)._
