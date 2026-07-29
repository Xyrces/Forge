# branch-audit

Enumerate every remote branch on `origin` and produce a deterministic audit
snapshot:

- `docs/BRANCH_AUDIT.md` — human-readable table, one row per branch.
- `docs/branch-audit.json` — machine-readable sidecar consumed by the sibling
  prune tool (task-35).

The tool does **not** delete anything. The prune step is a separate task so
the audit can be reviewed before any destructive action.

## Usage

```bash
dotnet run --project tools/branch-audit -- \
    --clone . \
    --default-branch main \
    --output-md docs/BRANCH_AUDIT.md \
    --output-json docs/branch-audit.json
```

The default values assume the current working directory is a git clone with
`origin` configured, and the default branch is `main`.

## Classification

| Pattern            | Category   |
|--------------------|------------|
| `polecat/*`        | `polecat`  |
| `convoy/*`         | `convoy`   |
| `gt*` (e.g. `gt1`) | `gt`       |
| `ph-*` / `ph/*`    | `ph`       |
| `agent/*`          | `agent`    |
| `POR-*` (any case) | `POR-stale`|
| anything else      | `other`    |

## Protection

A branch is **protected** (and therefore never eligible for deletion) when:

- Its name is `main`, `master`, `develop`, or `HEAD`, **or**
- It appears in the configured protection list. The list can be supplied via
  `BRANCH_AUDIT_PROTECTION_FILE` (path to a JSON array of branch names) — by
  default empty, since wiring GitHub's branch-protection endpoint requires
  `GITHUB_TOKEN` and is owned by a follow-up task.

## Per-branch metadata captured

- `tip_sha` — from `git ls-remote --heads origin`.
- `last_commit_date` — committer date (`%cI`, ISO-8601) of the tip commit,
  captured via `git log -1 --format=%cI <sha>`. May be `null` if the commit
  is unreachable from the local clone.
- `merged_into_main` — true iff `git merge-base --is-ancestor <sha> origin/<default>`
  exits 0.

## Sort order

Protected branches first; then by category (alphabetical); then by
`last_commit_date` descending; then by branch name (ordinal).
