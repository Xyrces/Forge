# Separate GitHub identities for Forge

The controller supports two repository-scoped GitHub Apps. Workers receive neither private key. The publisher creates PRs, pushes controller-verified changes and merges subject to repository rules; the reviewer only publishes reviews through its separate client. An installation token is refreshed before expiry and every request explicitly restricts it to the configured repository. Once a repository is configured, authentication failures do not fall back to a personal token.

## Register and install (one-time human setup)

1. In GitHub Settings → Developer settings → GitHub Apps, register two private Apps with distinct names, for example `forge-publisher-<owner>` and `forge-reviewer-<owner>`. Use the repository URL as homepage. Disable webhook delivery here until the authenticated event endpoint is configured; reconciliation still operates. This setup does not implement webhook ingress.
2. Publisher repository permissions: Contents **write**, Pull requests **write**, Issues **write** (labels), Workflows **write** (Forge can change its own workflows), Checks **read**, Commit statuses **read**, Metadata **read**. Do not grant Administration permission or add it to a ruleset bypass list.
3. Reviewer repository permissions: Contents **read**, Pull requests **write**, Checks **read**, Commit statuses **read**, Metadata **read**. No Contents write, Issues write, Administration or Workflows permission. GitHub's Pull requests write permission itself is broader than review submission; Forge restricts use of this credential to review operations.
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

The new deployment mounts this optional Secret only in controller pods and supplies `FORGE_GITHUB_APPS_FILE` from its `config-path` key. Absence of the entire Secret preserves legacy project authentication. A configured but missing/invalid file fails explicitly. Deploy a new qualified controller revision to activate or change the App mapping. Initial fresh-install bootstrap can still use its existing provisioning credential; steady-state clone/sync and trusted push use App credentials.

7. In a disposable protected repository, install the same Apps with only that repository selected in their token request configuration. Verify a publisher-authored PR receives a formal reviewer approval, a human request for changes stops merging, and a correction requires approval of the new head. Verify the actual merge occurs without a bypass. Only then switch Forge's production project to these credentials. Preserve any active human holds during rollout or rollback.

Remove obsolete persistent personal-token credential files from the trusted controller clone during the explicit migration after confirming other services do not depend on them. App tokens themselves are never written to those files: Git uses a repository-URL-scoped authorization header provided only to the controller's child process.

References: [installation tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app), [installation Git authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation), [App manifest registration](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest).

## Guided private setup

`scripts/github-app-setup.py --directory <new-private-directory> --port 18743` runs on the Linux host and binds only `127.0.0.1`. Forward that exact loopback port through SSH. Open the URL in the private `session-url` file. The page first links to existing owned App registrations so suitable Apps can be reused instead of creating duplicates. Creation requires an explicit GitHub confirmation for each role.

The helper uses GitHub's manifest exchange with one-use CSRF state and a private browser cookie. It stores each generated PEM with mode0600 in a mode0700 directory; unused OAuth secrets are discarded. On installation it validates App identity, owner and explicit repository selection, obtains a metadata-only audit token to verify the entire installation contains exactly `Xyrces/Forge`, and revokes that token immediately. Only after both roles validate does it stage `config.json`. It never applies a Kubernetes Secret or deploys. The session ends after two hours. Temporary forwarding allowances must be removed when setup ends (the operator's setup schedules that cleanup).

Run the offline helper qualification on Linux with `python3 tests/deployment/test_github_app_setup.py`; it performs no GitHub registrations or paid model calls. Tests cover state/replay rejection, private file modes, distinct role permissions, exact repository checks, audit-token revocation and staged config. A browser walkthrough still verifies GitHub's real manifest/installation screens.
