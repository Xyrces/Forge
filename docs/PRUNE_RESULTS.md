# Prune results — task-35

_Executed at `2026-07-29T13:07:06.416263+00:00`. Source of truth: `docs/branch-audit.json` (generated `2026-07-29T12:44:30.5465864Z` by `tools/branch-audit`)._

## Reconciliation

- **Total at audit time**: 39
- **Pruned (this run)**: 6
- **Kept** (unmerged + non-protected): 32
- **Protected** (never eligible): 1
- **Reconciliation**: 6 + 32 + 1 = 39 (must equal 39) → **OK**

## Per-branch result

### 1. `$ git push origin --delete agent/story-1`

- tip_sha: `c1f722d`
- exit code: `0` → **OK**
  - `remote:   https://github.com/Xyrces/Forge.git        `
  - `To https://github.com/forge.git`
  - ` - [deleted]         agent/story-1`

### 2. `$ git push origin --delete agent/task-188`

- tip_sha: `644ae67`
- exit code: `0` → **OK**
  - `remote:   https://github.com/Xyrces/Forge.git        `
  - `To https://github.com/forge.git`
  - ` - [deleted]         agent/task-188`

### 3. `$ git push origin --delete agent/task-202`

- tip_sha: `5564c84`
- exit code: `0` → **OK**
  - `remote:   https://github.com/Xyrces/Forge.git        `
  - `To https://github.com/forge.git`
  - ` - [deleted]         agent/task-202`

### 4. `$ git push origin --delete agent/task-210`

- tip_sha: `456257c`
- exit code: `0` → **OK**
  - `remote:   https://github.com/Xyrces/Forge.git        `
  - `To https://github.com/forge.git`
  - ` - [deleted]         agent/task-210`

### 5. `$ git push origin --delete agent/task-30`

- tip_sha: `207c254`
- exit code: `0` → **OK**
  - `remote:   https://github.com/Xyrces/Forge.git        `
  - `To https://github.com/forge.git`
  - ` - [deleted]         agent/task-30`

### 6. `$ git push origin --delete agent/task-31`

- tip_sha: `207c254`
- exit code: `0` → **OK**
  - `remote:   https://github.com/Xyrces/Forge.git        `
  - `To https://github.com/forge.git`
  - ` - [deleted]         agent/task-31`

## Post-prune remote state

After the 6 deletes, `git ls-remote --heads origin` reports **33** heads (1 protected + 32 kept):

```
033e14b  agent/task-50
1ca3317  agent/task-162
207c254  agent/task-32
227a0c3  agent/task-46
2936021  agent/task-65
3139e1b  agent/task-6
3921da5  main
3a25487  agent/task-64
4646f27  agent/task-205
4c009fd  agent/task-90
4e234ff  agent/task-149
51008cb  agent/task-66
5814682  agent/task-12
620057e  agent/task-164
6c6066a  agent/task-9
703f288  agent/task-85
731e7c4  agent/task-14
82ff959  agent/task-2
96b29d6  agent/task-79
9796ba3  agent/task-60
9ca0dd9  agent/task-148
a2835fc  agent/task-40
a49b789  agent/task-70
b31d3f5  agent/task-58
b4aef14  agent/task-72
ba0c026  agent/task-49
c035566  agent/task-161
c44202c  agent/task-45
ca016bb  agent/task-11
d56df57  agent/task-88
d8618d6  agent/task-78
d93b300  agent/task-35
ddc9e8a  agent/task-84
e96518d  agent/task-55
fff9df4  test/push-agent-12
```

## Out of scope (not done)

- No tag deletion.
- No local-branch deletion (this worktree keeps `agent/task-35`).
- No branch-protection changes.
- No history rewrite / force-push.

