# Separate GitHub identities for Forge

The controller supports two repository-scoped GitHub Apps. Workers receive neither private key. The publisher creates PRs, pushes controller-verified changes and merges subject to repository rules; the reviewer only publishes reviews through its separate client. An installation token is refreshed before expiry and every request explicitly restricts it to the configured repository. Once a repository is configured, authentication failures do not fall back to a personal token.

## Register and install (one-time human setup)

1. In GitHub Settings → Developer settings → GitHub Apps, register two private Apps with distinct names, for example `forge-publisher-<owner>` and `forge-reviewer-<owner>`. Use the repository URL as homepage. Disable webhook delivery here until the authenticated event endpoint is configured; reconciliation still operates. This setup does not implement webhook ingress.
2. Publisher repository permissions: Contents **write**, Pull requests **write**, Issues **write** (labels), Workflows **write** (Forge can change its own workflows), Checks **read**, Actions **read** (download release workflow artifacts), Commit statuses **read**, Metadata **read**. Do not grant Administration permission or add it to a ruleset bypass list.
3. Reviewer repository permissions: Contents **read**, Pull requests **write**, Checks **read**, Commit statuses **read**, Metadata **read**. No Contents write, Issues write, Actions, Administration or Workflows permission. GitHub's Pull requests write permission itself is broader than review submission; Forge restricts use of this credential to review operations.
4. Install each App on **Only select repositories** → Forge. Record App ID, App slug and installation ID separately. Generate a private key for each App and place each PEM in a controller provisioning directory with restricted access. Never paste PEMs into chat, commit them, put them in project files or mount them in workers.
5. Save `config.json` in that private provisioning directory, replacing all values:

```json
{"repositories":[{"owner":"Xyrces","name":"Forge","publisher":{"appId":123,"installationId":456,"slug":"forge-publisher-example","privateKeyFile":"/run/forge-github-apps/publisher.pem"},"reviewer":{"appId":789,"installationId":1011,"slug":"forge-reviewer-example","privateKeyFile":"/run/forge-github-apps/reviewer.pem"}}]}
```

6. From the trusted administrator terminal, create the namespace Secret without writing credentials into a command argument:

```bash
kubectl -n forge create secret generic forge-github-apps \
  --from-file=config.json=./config.json \
  --from-file=publisher.pem=./publisher.pem \
  --from-file=reviewer.pem=./reviewer.pem \
  --from-literal=config-path=/run/forge-github-apps/config.json \
  --dry-run=client -o yaml | kubectl apply -f -
```

The controller mounts the optional Secret and reads `FORGE_GITHUB_APPS_FILE` from its `config-path` key. Deploy a qualified controller revision to activate or change an App mapping. A configured but missing or invalid file fails the preflight check.

For a repository with personal-token authentication, select **Human reviewer** in **Project overview → Pull request approvals**. An eligible human other than the author must approve the current revision. The default **Forge reviewer** policy and **Forge and human reviewers** both require distinct publisher and reviewer Apps. Without that mapping, the project control explains what to fix, `--check` reports the affected project, and Forge holds its engineering tasks before assigning a worker. Other projects can continue. Fresh-install bootstrap can use its existing provisioning credential.

7. In a disposable protected repository, install the same Apps with only that repository selected in their token request configuration. Verify a publisher-authored PR receives a formal reviewer approval, a human request for changes stops merging, and a correction requires approval of the new head. Verify the actual merge occurs without a bypass. Only then switch Forge's production project to these credentials. Preserve any active human holds during rollout or rollback.

Remove obsolete persistent personal-token credential files from the trusted controller clone during the explicit migration after confirming other services do not depend on them. App tokens themselves are never written to those files: Git uses a repository-URL-scoped authorization header provided only to the controller's child process.

### Controller Git boundary

App mode requires Linux and Kubernetes workers. Repository verification commands never run in the credentialed controller. Controller Git imports approved metadata and object files into a mode0700 directory on the controller's private `/tmp`; it never imports worker configuration, hooks, attributes overrides, alternate object directories, helpers or replacement refs. Child processes use `/usr/bin/git`, an empty inherited environment with explicit safe defaults, and the immutable configured HTTPS repository URL. Redirects and submodule recursion are disabled. A push validates imported objects before obtaining credentials. The worker sees neither App keys nor installation tokens.

Receipt checks receive the registered repository root, compare the worker's verified tree with a controller-computed tree, and keep their private index separate. Shared source remains untrusted; source changes still invalidate the existing publication checks. Only changed private Git data is returned. The complete mutable write set is locked in sorted order, every expected source hash is checked, and replacement files are staged before publishing; a conflicting later file cannot overwrite earlier shared state. Each replacement is atomic. Read-only operations return no data, and imported worktree pointers never determine output destinations. Configuration is never copied back.

The initial supported layout is a normal SHA1 project clone with engineering worktrees beneath its managed root (explicitly authorized separate worktree roots are supported by GitWorktreeService). Linked Git metadata paths, alternates, symlink-containing clone imports, non-Linux hosts, and files above 512 MiB fail closed. Linux `openat2` support is required. A failed private clone does not fall back to executing a shared scaffold. Custom Git filters and hooks intentionally do not execute in the controller. The initial implementation copies objects per operation; performance must be measured on larger project repositories before enabling them.

References: [installation tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app), [installation Git authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation), [App manifest registration](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest).

## Guided private setup

With the App configuration mounted, `--check` uses the registered project's
repository and resolves both installation credentials. It reads repository and
pull request metadata with each role and reads workflow metadata with the
publisher. A failed App check identifies the repository, role and stage and
returns a failing exit code. The check creates no PR or review. A protected
pilot is still needed to exercise publishing, inline review and merge.

`scripts/github-app-setup.py --directory <new-private-directory> --port 18743` runs on the Linux host and binds only `127.0.0.1`. Forward that exact loopback port through SSH. Open the URL in the private `session-url` file. The page first links to existing owned App registrations so suitable Apps can be reused instead of creating duplicates. Creation requires an explicit GitHub confirmation for each role.

The helper uses GitHub's manifest exchange with one-use CSRF state and a private browser cookie. It stores each generated PEM with mode0600 in a mode0700 directory; unused OAuth secrets are discarded. On installation it validates App identity, owner and explicit repository selection, obtains a metadata-only audit token to verify the entire installation contains exactly `Xyrces/Forge`, and revokes that token immediately. Only after both roles validate does it stage `config.json`. It never applies a Kubernetes Secret or deploys. The session ends after two hours. Temporary forwarding allowances must be removed when setup ends (the operator's setup schedules that cleanup).

Run the offline helper qualification on Linux with `python3 tests/deployment/test_github_app_setup.py`; it performs no GitHub registrations or paid model calls. Tests cover state/replay rejection, private file modes, distinct role permissions, exact repository checks, audit-token revocation and staged config. A browser walkthrough still verifies GitHub's real manifest/installation screens.


## Optional nominated human approver

Project overview → Pull request approvals has an optional human GitHub login.
When configured, that eligible person's current-head formal approval is required
whenever Human/Both policy or an additional task hold requires human review.
A different eligible collaborator's approval cannot substitute. Every eligible
human's outstanding formal objection still blocks, even if the nominee approved.
The PR author and bots never count as the human approver. No username is built in.

The authenticated review-policy endpoint reads/writes `nominatedHuman` alongside
`mode`; both are committed atomically with the immutable operator audit in the
project's protected `review/` memory. Omitting the field preserves its current
value; null or blank explicitly clears it and restores any-eligible-human
fallback without changing the selected mode. Older stored mode-only values remain
readable. Invalid logins are rejected. A valid-looking but ineligible/absent login
cannot satisfy the approval gate and should be corrected in the project control.

Cards and task detail name the configured login in the review action and show
that person as next actor only when human review is actually actionable. Adding
an extra hold while engineering is running does not stop engineering; releasing
that hold cannot waive project-wide human approval.

## Protected approval waits

A submitted reviewer App approval is not necessarily an eligible approval under
GitHub branch protection. Before merging, Forge reads the current-head GraphQL
`reviewDecision` using its publisher identity. `REVIEW_REQUIRED` and
`CHANGES_REQUESTED` retain the watch without merge attempts or paid rework strikes;
current-head findings, failed CI and conflicts still follow normal correction.
Forge's own human holds, nominated reviewers and unresolved human objections also
remain required, including repositories with no protected-review requirement.

The board/task situation distinguishes “GitHub approval required”, “GitHub changes
requested”, and an unavailable protection check. A separately required named human
remains the next actor. Missing, stale-head, unknown or error responses fail closed
without inventing CI results or approvals. Refused merges refresh this wait state.
No branch rules, credentials or bypass permissions are changed.
