# Separate GitHub identities for Forge

The controller supports two repository-scoped GitHub Apps. Workers receive neither private key. The publisher creates PRs, pushes controller-verified changes and merges subject to repository rules; the reviewer only publishes reviews through its separate client. An installation token is refreshed before expiry and every request explicitly restricts it to the configured repository. Once a repository is configured, authentication failures do not fall back to a personal token.

## Register and install (one-time human setup)

1. In GitHub Settings → Developer settings → GitHub Apps, register two private Apps with distinct names, for example `forge-publisher-<owner>` and `forge-reviewer-<owner>`. Use the repository URL as homepage. Disable webhook delivery here until the authenticated event endpoint is configured; reconciliation still operates. This setup does not implement webhook ingress.
2. Publisher repository permissions: Contents **write**, Pull requests **write**, Issues **write** (labels), Workflows **write** (Forge can change its own workflows), Checks **read**, Actions **read** (download release workflow artifacts), Commit statuses **read**, Metadata **read**. Do not grant Administration permission or add it to a ruleset bypass list.
3. Reviewer repository permissions: Contents **read**, Pull requests **write**, Checks **read**, Commit statuses **read**, Metadata **read**. No Contents write, Issues write, Actions, Administration or Workflows permission. GitHub's Pull requests write permission itself is broader than review submission; Forge restricts use of this credential to review operations.
4. Install each App on **Only select repositories** and select the repository you want Forge to develop. Record each App ID, App slug and installation ID. The App ID is on its settings page, the slug appears in its public App URL, and the installation ID appears in the installation settings URL. Generate a private key for each App.
5. On the Linux host, prepare the private bundle directory using the `forge_setup` location from the [installation quickstart](../README.md#installation-quickstart).

```bash
install -d -m 700 "$forge_setup/github-apps"
```

Copy the downloaded publisher key into that directory as `publisher.pem` and the reviewer key as `reviewer.pem`. Keep them outside your project checkout. Create `config.json` alongside them, replacing the example owner, repository, numbers and App slugs below. Preserve the `/run/forge-github-apps/` paths, which are the keys' locations inside the controller.

```json
{"repositories":[{"owner":"your-owner","name":"your-repository","publisher":{"appId":123,"installationId":456,"slug":"your-publisher-app","privateKeyFile":"/run/forge-github-apps/publisher.pem"},"reviewer":{"appId":789,"installationId":1011,"slug":"your-reviewer-app","privateKeyFile":"/run/forge-github-apps/reviewer.pem"}}]}
```

Protect all three files, then supply the bundle when installing Forge.

```bash
chmod 600 "$forge_setup/github-apps/config.json" \
  "$forge_setup/github-apps/publisher.pem" "$forge_setup/github-apps/reviewer.pem"
```

6. Complete the bundle before running the quickstart's preview and first `--apply`. Its install command includes the bundle through `--github-apps-directory "$forge_setup/github-apps"` when the directory is present. The installer creates the namespace and the controller's private Secret. The controller reads it at startup. Existing installations require an explicit configuration-maintenance operation to change the bundle and a controller replacement to load it. A configured but missing or invalid file fails preflight.

For a repository with personal-token authentication, select **Human reviewer** in **Project overview → Pull request approvals**. An eligible human other than the author must approve the current revision. The default **Forge reviewer** policy and **Forge and human reviewers** both require distinct publisher and reviewer Apps. Without that mapping, the project control explains what to fix, `--check` reports the affected project, and Forge holds its engineering tasks before assigning a worker. Other projects can continue. Fresh-install bootstrap can use its existing provisioning credential.

7. Register the configured repository in **Projects → Add Project** and choose **Forge reviewer** in its **Pull request approvals** settings. Start with a small change and inspect the resulting PR. It should be authored by the publisher App, reviewed by the reviewer App and merged after the repository's checks and review rules pass. A human request for changes blocks merging until a correction receives the required approvals. An additional test repository needs its own entry in `repositories` and both Apps installed there.

Remove obsolete persistent personal-token credential files from the trusted controller clone during the explicit migration after confirming other services do not depend on them. App tokens themselves are never written to those files: Git uses a repository-URL-scoped authorization header provided only to the controller's child process.

### Controller Git boundary

App mode requires Linux and Kubernetes workers. Repository verification commands never run in the credentialed controller. Controller Git imports approved metadata and object files into a mode0700 directory on the controller's private `/tmp`; it never imports worker configuration, hooks, attributes overrides, alternate object directories, helpers or replacement refs. Child processes use `/usr/bin/git`, an empty inherited environment with explicit safe defaults, and the immutable configured HTTPS repository URL. Redirects and submodule recursion are disabled. A push validates imported objects before obtaining credentials. The worker sees neither App keys nor installation tokens.

Receipt checks receive the registered repository root, compare the worker's verified tree with a controller-computed tree, and keep their private index separate. Shared source remains untrusted; source changes still invalidate the existing publication checks. Only changed private Git data is returned. The complete mutable write set is locked in sorted order, every expected source hash is checked, and replacement files are staged before publishing; a conflicting later file cannot overwrite earlier shared state. Each replacement is atomic. Read-only operations return no data, and imported worktree pointers never determine output destinations. Configuration is never copied back.

The initial supported layout is a normal SHA1 project clone with engineering worktrees beneath its managed root (explicitly authorized separate worktree roots are supported by GitWorktreeService). Linked Git metadata paths, alternates, symlink-containing clone imports, non-Linux hosts, and files above 512 MiB fail closed. Linux `openat2` support is required. A failed private clone does not fall back to executing a shared scaffold. Custom Git filters and hooks intentionally do not execute in the controller. The initial implementation copies objects per operation; performance must be measured on larger project repositories before enabling them.

References: [installation tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app), [installation Git authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation), [App manifest registration](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest).

## Guided private setup

The helper below is configured specifically for `Xyrces/Forge`. Use the manual steps above for your own repository.

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
