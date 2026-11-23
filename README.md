# Forge

Forge runs AI agents on your own infrastructure. You describe a change and approve what counts as done. Forge plans a sprint, writes and tests the code, and opens a pull request. You can follow the work on the sprint board and step in for a review.

## Installation quickstart

You'll install Forge on one Linux machine, open its private dashboard and add a repository you own. The installer sets up K3s and starts Forge with the work gates held. You choose when agents can start building.

**What you need**

- An x86-64 Linux host with systemd, sudo, Bash, Python 3.9 or newer, Git, `jq` and the [GitHub CLI](https://cli.github.com/). Use a regular login. The installation requests a 40 GiB volume for Forge.
- An Azure subscription where you can create a SQL database and manage its network access.
- A GitHub account with read access to the published Forge container image and private Talaria packages. A package token needs that underlying access even though the Forge repository is public.
- For a real first sprint, an OpenAI API key and a GitHub repository where you can install Apps. You can explore the dashboard before enabling model calls.

### 1. Get the installer and matching release

Run these steps in **one Bash session on the Linux host**. Use `gh auth login` if you haven't signed in there. The image receipt comes from a successful Forge build and identifies the exact version to install. [Image builds on main](https://github.com/Xyrces/Forge/actions/workflows/controller-image.yml?query=branch%3Amain)

```bash
git clone https://github.com/Xyrces/Forge.git
cd Forge
umask 077
forge_setup="$HOME/.config/forge-install"
install -d -m 700 "$forge_setup"
run_id=$(gh run list --repo Xyrces/Forge --workflow controller-image.yml --branch main \
  --event push --status success --limit 1 --json databaseId --jq '.[0].databaseId')
release="$forge_setup/releases/$run_id"
receipt="$release/forge-image-receipt.json"
if [[ ! -s "$receipt" ]]; then
  gh run download "$run_id" --repo Xyrces/Forge --name forge-image-receipt --dir "$release"
fi
if [[ ! -d "$release/source" ]]; then
  git worktree add --detach "$release/source" "$(jq -er .sourceSha "$receipt")"
fi
```

If the download reports an expired or missing artifact, choose a successful `main` build with an available **forge-image-receipt** in the image builds link above. Use that run's number for `run_id` and repeat the download and checkout.

### 2. Prepare your database and credentials

Create an empty Azure SQL database in the portal and allow the Linux host's public outbound IP. Save the server login you create. You'll use it once to give Forge access. [Portal steps and database permissions](deploy/k3s/README.md#create-the-database-and-its-three-logins)

Generate your private installation files. Have the database name and server hostname ready, along with your GitHub username, a classic token with `read:packages`, and your OpenAI key. To explore the dashboard with paid calls off, omit `--enable-models`. Choose before installing because ordinary upgrades reuse these settings. [GitHub package access](https://docs.github.com/en/packages/learn-github-packages/introduction-to-github-packages#authenticating-to-github-packages)

```bash
inputs="$forge_setup/inputs"
python3 docs/examples/prepare-first-install.py "$inputs" \
  --template "$release/source/deploy/k3s/appsettings.first-install.json" --enable-models
```

1. Open `$inputs/create-users.sql` in your editor. In the Azure portal, open **your database → Query editor**, sign in with the server login, paste the SQL and choose **Run**. Success lists three users for initialization, the controller and workers. Keep the script private. It contains their generated passwords.
2. For real work, review `Budget.LimitUsd` and `Budget.MaximumRequestUsd` in `$inputs/appsettings.json`. The example starts at **$25 total and $2 per request**. Register the publisher and reviewer Apps on **your** repository and save their bundle in `$forge_setup/github-apps`. One person can own both Apps. [GitHub App setup](docs/github-app-identities.md#register-and-install-one-time-human-setup)

### 3. Install and open the dashboard

Preview the installation. This calls the selected release's installer and checks your files against its image receipt. Look for `"mode": "plan"` and the revision you selected.

```bash
bash docs/examples/install-first-release.sh "$forge_setup" "$run_id"
```

When the preview looks right, apply it on the **Linux host**. Keep the same inputs and run ID for a retry. After `Release completed`, the second command prints the private service IP.

```bash
sudo -v && bash docs/examples/install-first-release.sh "$forge_setup" "$run_id" --apply &&
  sudo /usr/local/bin/k3s kubectl -n forge get svc forge -o jsonpath='{.spec.clusterIP}{"\n"}'
```

On **your workstation**, substitute that IP and your SSH login below. Leave the terminal open and visit [the dashboard](http://127.0.0.1:4097). The [build information](http://127.0.0.1:4097/api/meta/buildinfo) should show a `sourceRevision` matching the receipt. The SSH tunnel keeps the dashboard private.

```sh
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:4097:FORGE_SERVICE_IP:8080 your-user@your-linux-host
```

If you're browsing on the Linux host itself, run `sudo /usr/local/bin/k3s kubectl -n forge port-forward --address 127.0.0.1 svc/forge 8080:8080` and open [the local dashboard](http://127.0.0.1:8080).

## Give Forge its first piece of work

The first project in the selector is **Forge** itself. Open **Projects → Add Project**, register your own repository and select it in the top bar. In **Intake → New idea**, describe one small change and how you'll recognize success. For example, “Add a search box to the issue list. When I clear it, I should see all the issues again.”

Read the **Working brief** beside the conversation. Ask for changes there, then choose **Approve revision** when it describes the work you want. **Open specification** shows what went into planning. When you're ready for execution, release the relevant stage holds and follow the cards in **Sprints**. [First-initiative walkthrough](docs/user-guide.md#run-your-first-initiative)

## Where to go next

| I want to… | Guide |
| --- | --- |
| Shape the project vision, follow a sprint or review a PR | [Using Forge](docs/user-guide.md) |
| Give workers a compiler or other project-specific tools | [Project worker Dockerfiles](deploy/k3s/README.md#project-worker-tools) |
| Change models, spending limits or stage gates | [Runtime reference](docs/runtime-reference.md) |
| Set up UAT and promote a release to staging or production | [Deployment environments](docs/deployment-environments.md) |
| Upgrade Forge or recover an installation | [K3s operations](deploy/k3s/README.md#independent-release-and-rollback) |
| Work on Forge itself | [Contributor quickstart](docs/getting-started.md#build-and-test-the-code) |

[All documentation](docs/README.md) · [A real sprint from review through correction](docs/workflow-audit/pr150-history.md#review-decisions-and-revisions)
