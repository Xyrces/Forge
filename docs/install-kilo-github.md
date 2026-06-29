# Install the Kilo GitHub App (one-time, manual)

The orchestrator's PRWatcher only **merges** PRs after Kilo reviews them. The review itself is performed by the **Kilo GitHub App**, which is installed per-repository. This is a one-time admin action and **cannot be done unattended by the orchestrator** — it requires GitHub authentication from a user who can install apps on the repo or org.

## Prerequisites

- A host with the [kilo CLI](../install-kilo.md) installed.
- A GitHub account with admin (or owner) rights on the target repository.
- The `gh` CLI authenticated as that admin account:
  ```bash
  gh auth status
  ```

## Install

```bash
# From any host:
kilo github install --repo Xyrces/PortHorizon
```

Follow the printed URL to authorize the Kilo GitHub App on the org. Approve the requested permissions:

- **Read** access to code, pull requests, and issues (required)
- **Write** access to pull requests (so the App can post reviews and check statuses)

If you manage multiple repositories, install once per repo, or install at the org level and grant access to all current and future repositories.

## Verify

```bash
kilo github status
# Expected output includes: "Installed on Xyrces/PortHorizon ✓"
```

If `kilo github status` is not available in your CLI version, verify manually:

1. Open `https://github.com/organizations/Xyrces/settings/installations` (or the repo's *Settings → Integrations → Applications*).
2. Confirm **Kilo** is listed and authorized for `Xyrces/PortHorizon`.

## What happens after install

- Every push to a PR triggers a Kilo review.
- Kilo posts a single review comment with `APPROVE` or `REQUEST_CHANGES`.
- The orchestrator's PRWatcher polls every 30 s and merges on green CI + approval.
- If the App is uninstalled, the orchestrator will time out PRs after the configured `StaleMinutes` (default 30 minutes).

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| No review comment after PR is opened | App not installed or not authorized on the repo | Re-run `kilo github install` and check repo access |
| Review is posted but `REQUEST_CHANGES` always | Architecture violation in PR | Read the reviewer's findings; rework and push |
| PR sits in `Pending` forever | No CI configured on the repo, or CI does not report to the PR's head SHA | Configure a GitHub Actions workflow that runs on `pull_request` |
| `kilo github install` returns 404 | Org or repo name typo | Confirm `Xyrces/PortHorizon` exists; rename or update the config |