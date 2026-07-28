# Linux deployment — systemd

Forge runs as a single long-lived process supervised by systemd
(Type=notify). This doc covers the install, upgrade, and ops
workflow. For the design rationale (why systemd over Kubernetes),
see `AGENTS.md` § "Linux daemon vs Kubernetes" (or the commit message
that introduced the systemd executor).

## Layout

```
/opt/forge/
  releases/<sha>/          published release directories (one per deploy)
  current -> releases/...  symlink, what the unit runs

/etc/forge/
  appsettings.json         runtime config (mode 0640, owned by root:forge)
  forge.env                secrets (mode 0600, root:forge; optional)

/var/lib/forge/            StateDirectory — SQLite state, JSONL mirror, worktrees
  ├── state/
  │   ├── issues.db
  │   ├── memory.db
  │   └── issues.jsonl
  └── ...

deploy/systemd/
  forge.service            unit template, copied to /etc/systemd/system/ by the installer

scripts/
  install-systemd-service.sh
  uninstall-systemd-service.sh
```

The target project (e.g. `PortHorizon/`) is **not** under
`/opt/forge` — it lives wherever your `workspace.root` says
(`/home/jtn5016/repos/gamedev/PortHorizon` in the example
`appsettings.multi-project.example.json`). The unit only owns the
orchestrator + its state dir.

## First install

1. **Create the unprivileged user** (the installer does this for
   you, but if you want to set it up out-of-band):

   ```bash
   sudo useradd --system \
       --home /var/lib/forge \
       --shell /usr/sbin/nologin \
       --comment "Forge orchestrator" \
       forge
   ```

2. **Publish a release**:

   ```bash
   dotnet publish Forge.Core.csproj -c Release -o /tmp/forge-$(git rev-parse --short HEAD)
   ```

3. **Drop an appsettings.json**:

   ```bash
   sudo cp appsettings.example.json /etc/forge/appsettings.json
   sudo chmod 0640 /etc/forge/appsettings.json
   sudo chown root:forge /etc/forge/appsettings.json
   $EDITOR /etc/forge/appsettings.json   # fill in kilo gateway key + GitHub PAT
   ```

   For multi-project setups, see
   `appsettings.multi-project.example.json` and `docs/multi-project.md`.

4. **Install + start the service**:

   ```bash
   sudo scripts/install-systemd-service.sh \
       --release-dir /tmp/forge-$(git rev-parse --short HEAD)
   ```

   The installer:
   - Creates `/opt/forge/releases/<sha>/` + repoints `/opt/forge/current`
   - Installs `/etc/systemd/system/forge.service`
   - `systemctl daemon-reload`
   - `systemctl enable --now forge`
   - Prints `systemctl status forge` so you can confirm.

5. **Verify**:

   ```bash
   sudo systemctl status forge
   sudo journalctl -u forge -f
   curl -sf http://127.0.0.1:4097/api/state | jq .
   ```

## Upgrades

The `SelfHostedSystemdService` deployment executor is the
**operator-driven** path — you approve a candidate via the dashboard
(`Deployments` page), and the executor builds, publishes, repoints
the symlink, and runs `systemctl restart forge`. Service is briefly
unavailable (typically 1-3s); the dashboard's SSE reconnects
automatically.

The **scripted** path is identical to the Windows one — re-run the
install script with a new `--release-dir`. The installer is
idempotent and repoints the symlink atomically:

```bash
sudo scripts/install-systemd-service.sh \
    --release-dir /tmp/forge-$(git rev-parse --short HEAD)
```

The service is restarted as part of the install (unless you pass
`--no-start`).

## Operations

```bash
# Liveness
sudo systemctl status forge
sudo systemctl is-active forge
sudo systemctl is-enabled forge

# Logs (journald)
sudo journalctl -u forge -f                 # live tail
sudo journalctl -u forge --since "1 hour ago"
sudo journalctl -u forge -n 1000 --no-pager

# Restart
sudo systemctl restart forge                # used by SelfHostedSystemdService deploys
sudo systemctl reload forge                 # no-op; forge doesn't watch for SIGHUP

# Stop
sudo systemctl stop forge                   # graceful, 30s timeout
```

## Secrets

The unit does **not** read `appsettings.json` for secrets (the JWT
goes through `KILO_GATEWAY_API_KEY`, the GitHub PAT through
`GITHUB_TOKEN`). Three options, in order of preference:

1. **EnvironmentFile** — drop secrets into `/etc/forge/forge.env`
   (mode 0600, root:forge), then add to the unit:

   ```ini
   # /etc/systemd/system/forge.service.d/secrets.conf
   [Service]
   EnvironmentFile=/etc/forge/forge.env
   ```

   The installer accepts `KILO_GATEWAY_API_KEY` and `GITHUB_TOKEN`
   from its own environment and writes them to `forge.env`
   automatically.

2. **`systemctl edit forge`** — drop into the override file (mode
   0600 by default).

3. **Plain appsettings.json** — fine for dev, never for prod.

## Reverse proxy / TLS

The default `ASPNETCORE_URLS=http://127.0.0.1:4097` binds to
loopback only. To expose the dashboard:

- **Nginx / Caddy / Traefik** in front of `127.0.0.1:4097` for TLS
  + auth. SSE requires HTTP/1.1 with no buffering (`proxy_buffering
  off;` for nginx).
- **Kestrel HTTPS** — set `ASPNETCORE_URLS=https://...:443` and
  configure the Kestrel HTTPS endpoint in `appsettings.json`. You
  need a cert (`dotnet dev-certs https` for dev; Let's Encrypt or
  an internal CA for prod).

## Azure SQL state backend (optional)

With `db.provider=sqlserver` the per-project SQLite files are replaced
by one Azure SQL database (schema `proj_<id>` per project). See
`docs/azure-sql-cutover.md` for the full runbook (resources, cutover
steps, failure modes). Deployment-specific notes:

- Auth is Entra-only; the connection string uses
  `Authentication=Active Directory Default` — on a self-hosted machine
  this resolves via the Azure CLI login of the service user; in Azure
  it resolves via managed identity (`forge-mi`, already provisioned as
  db_owner).
- `scripts/refresh-sql-firewall.sh` keeps the server's
  `forge-dev-machine` firewall rule aligned with a dynamic egress IP
  and refreshes the az token. Wire it as an ExecStartPre (commented
  example in `deploy/systemd/forge.service`) and install
  `deploy/systemd/forge-sql-firewall.{service,timer}` for the 15-min
  refresh.
- Backup becomes Azure-side (automatic backups + PITR on the Basic
  tier). The SQLite guidance below applies only to the default
  `sqlite` provider.

## Backup

State lives in three places; back them all up:

- `/var/lib/forge/state/` (issues.db, memory.db, issues.jsonl)
- `/etc/forge/appsettings.json` + `forge.env`
- The target projects' working trees (Forge creates git worktrees
  under `workspace.worktreeRoot`, default `.portHorizon/worktrees/`).

SQLite is in WAL mode; `sqlite3 issues.db ".backup '/path'"` is the
safe online-backup procedure. Don't copy `issues.db` while the
service is running — `.backup` is the only race-free way.

## What this replaces

The Windows-era bits that are gone as of this commit:

- `Microsoft.Extensions.Hosting.WindowsServices` → swapped for
  `Microsoft.Extensions.Hosting.Systemd`.
- `DeploymentKind.SelfHostedWindowsService` →
  `DeploymentKind.SelfHostedSystemdService`.
- `SelfHostedWindowsServiceDeploymentExecutor` →
  `SelfHostedSystemdServiceDeploymentExecutor` (no detached helper
  needed — systemd restart is synchronous under `Type=notify`).
- `tools/Forge.Deployer` → deleted (its entire purpose was the
  Windows-SCM self-deadlock dance; systemd stops one's own service
  cleanly).
- `scripts/install-service.ps1` / `uninstall-service.ps1` →
  `install-systemd-service.sh` / `uninstall-systemd-service.sh`.
- `DeploymentResultReconciler` → deleted (it existed to pick up the
  result file dropped by the detached Deployer helper; no detached
  helper means no result file means no reconciler).
- `Program.cs` `.pending-{sha}` marker swap → deleted (the systemd
  executor does the swap inline before `systemctl restart` returns).