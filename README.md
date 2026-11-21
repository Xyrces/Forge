# Forge

Forge is a self-hosted tool for developing software with AI agents. You describe a change, work out what counts as done, and approve the brief. Forge plans a sprint, assigns the work, and follows the pull requests through testing, review, and delivery. You can open the sprint board at any point to see where things stand or ask to review a change yourself.

We use Forge on its own repository. One sprint started with a 404 in the browser console. The dashboard was loading a tool that only developers needed. We wanted to keep it available in development and stop requesting it in production. The review caught an unrelated test edit. When the task got stuck, we put it back in the queue. The fix merged on the original pull request. You can follow the [actual review and correction](docs/workflow-audit/pr150-history.md#review-decisions-and-revisions).

## Installation quickstart

This installs Forge on one Linux host and gives you a private dashboard with Forge registered as its first project. The starter configuration keeps paid model calls disabled while you check the installation.

### 1. Prepare the host and database

Use an x86-64 Linux host with systemd, Bash, Python 3, Git, `jq`, and a regular login with sudo access. The installer can set up K3s with embedded etcd. You can also use an existing single-node K3s installation with embedded etcd. The host needs network access to GitHub, GHCR, and your Azure SQL database.

Start in a fresh checkout. Keep the installation files outside it.

```bash
git clone https://github.com/Xyrces/Forge.git
cd Forge
umask 077
forge_setup="$HOME/.config/forge-install"
install -d -m 700 "$forge_setup"
if [[ ! -e "$forge_setup/appsettings.json" ]]; then
  install -m 600 deploy/k3s/appsettings.first-install.json "$forge_setup/appsettings.json"
fi
```

Have a database administrator prepare an empty Azure SQL database and three users in it. The initializer needs permission to create schemas and grant access. The controller and workers use separate runtime accounts. Set the two runtime user names in your private `appsettings.json` to match the database. The sample sets a $25 spending ceiling with paid calls switched off. Choose the ceiling you want recorded for this new installation.

Put these files in `$forge_setup` before continuing. The [private file examples and SQL permissions](deploy/k3s/README.md#prepare-the-private-install-files) show the expected formats.

| Files | What goes in them |
| --- | --- |
| `sql-controller.txt`, `sql-initialize.txt`, `sql-worker.txt` | One connection string per file, all pointing to the same database, each using its own account. |
| `worker-packages.json` | A GitHub username and token with read access to the private Talaria packages used in worker builds. |
| `registry.json` | Docker-format registry credentials with read access to the Forge image in GHCR. |

### 2. Select a release and preview the install

Open a successful **Build immutable Forge controller image** run for the reviewed commit on `main`. Download its **forge-image-receipt** artifact and extract `forge-image-receipt.json` into `$forge_setup`. The receipt identifies the exact code revision and container image tested together. [Controller image builds](https://github.com/Xyrces/Forge/actions/workflows/controller-image.yml?query=branch%3Amain)

In the same Bash session, switch the installer to that revision and create a dataset ID. Keep the ID file for retries and future releases on this installation.

```bash
receipt="$forge_setup/forge-image-receipt.json"
git switch --detach "$(jq -er .sourceSha "$receipt")"
if [[ ! -e "$forge_setup/dataset-id" ]]; then
  python3 -c 'import uuid; print(uuid.uuid4())' > "$forge_setup/dataset-id"
fi
```

Define the install command once so the preview and apply use the same inputs. The K3s version and checksum below match the Linux host running Forge. If K3s is already installed, the installer checks the existing cluster. [K3s release and checksums](https://github.com/k3s-io/k3s/releases/tag/v1.37.2%2Bk3s1)

```bash
forge_install() {
  python3 deploy/k3s/install.py \
    --image "$(jq -er .imageDigest "$receipt")" \
    --source-sha "$(git rev-parse HEAD)" --image-receipt "$receipt" \
    --dataset "$(cat "$forge_setup/dataset-id")" --operation first-install \
    --sql-file "$forge_setup/sql-controller.txt" \
    --initialize-sql-file "$forge_setup/sql-initialize.txt" \
    --worker-sql-file "$forge_setup/sql-worker.txt" \
    --worker-packages-file "$forge_setup/worker-packages.json" \
    --registry-config "$forge_setup/registry.json" \
    --config "$forge_setup/appsettings.json" \
    --k3s-version 'v1.37.2+k3s1' \
    --k3s-sha256 '265b3ee6a3198ef14033c63ef46cbb32370d83795199fb6d1b01f21575eddbda' \
    "$@"
}
forge_install
```

Look for `"mode": "plan"`, namespace `forge`, and the source revision you chose. This checks the local inputs and their match to the receipt. The image pull and database connection happen when you apply the plan.

### 3. Install and open the dashboard

On the Linux host, apply the plan. After **Release completed**, the second command prints the private address of the Forge service.

```bash
forge_install --apply &&
  sudo /usr/local/bin/k3s kubectl -n forge get svc forge -o jsonpath='{.spec.clusterIP}{"\n"}'
```

On **your workstation**, replace `FORGE_SERVICE_IP` with that address and `your-user@your-linux-host` with your SSH login. Your SSH account needs permission to forward to that service on port 8080. Leave this terminal open while you use the dashboard.

```sh
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:4097:FORGE_SERVICE_IP:8080 your-user@your-linux-host
```

Open `http://127.0.0.1:4097` in your browser. You should see **Forge** in the project selector. At `http://127.0.0.1:4097/api/meta/buildinfo`, compare `sourceRevision` with `sourceSha` in your receipt. When they match, you’re looking at the release you installed.

If you’re using a browser **on the Linux host itself**, run `sudo /usr/local/bin/k3s kubectl -n forge port-forward --address 127.0.0.1 svc/forge 8080:8080` and open `http://127.0.0.1:8080` there.

The starter lets you explore the dashboard with the workflow gates held. Intake uses canned replies until a controller starts with a configured model key. To run real work, prepare the provider credentials, spending policy, and repository access, then restart the controller with the updated configuration before opening the gates. [Configuration and credential setup](deploy/k3s/README.md#prepare-the-private-install-files)

## How Forge works

1. **Give the project direction.** Use the Vision Board to describe what you're building and the design choices you want future work to respect. You can refine the mind map in conversation as the project takes shape.
2. **Agree on a change.** Start an idea in Intake. The conversation and working brief sit together so you can ask for a revision, see exactly what changed, and approve the version you want built.
3. **Follow the sprint.** Forge turns the approved work into tasks and runs independent tasks in parallel when workers are available. Each card links to the work behind it. If a change needs your eyes, you can require a human review while the sprint is underway.
4. **Decide where it ships.** Each project can choose its sprint deployment target. Forge itself closes the sprint when the release passes its deployment checks in the user acceptance testing (UAT) environment. From Deployments, you can pick that tested release and promote it to staging or production.

## Documentation

| When you're ready to… | Read |
| --- | --- |
| Give Forge its first initiative | [Using Forge](docs/user-guide.md) |
| Set up separate GitHub publisher and reviewer identities | [GitHub App setup](docs/github-app-identities.md) |
| Configure model access, spending, or logs | [Runtime reference](docs/runtime-reference.md) |
| Choose where a sprint deploys or promote a release | [Deployment environments](docs/deployment-environments.md) |
| Build Forge and run the tests from source | [Contributor quickstart](docs/getting-started.md#build-and-test-the-code) |
| Find architecture notes, runbooks, and design history | [Documentation index](docs/README.md) |
