#!/usr/bin/env bash
# Installs Forge as a systemd service on Linux.
#
# Stages a published release at /opt/forge/<sha>/, repoints
# /opt/forge/current at it, drops the unit file at
# /etc/systemd/system/forge.service, then enables + starts the
# service.
#
# Usage:
#   sudo scripts/install-systemd-service.sh \
#     --app-settings /etc/forge/appsettings.json \
#     --release-dir /opt/forge/releases/<sha>
#
# Arguments:
#   --app-settings PATH   Where to find appsettings.json (default: /etc/forge/appsettings.json)
#   --release-dir PATH    The release dir to point /opt/forge/current at (required)
#   --unit-name NAME      systemd unit name (default: forge)
#   --no-enable           Don't `systemctl enable` after install
#   --no-start            Don't `systemctl start` after install
#
# Idempotent: re-running with the same release-dir is a no-op; with
# a different release-dir it repoints the symlink + restarts.
#
# Must run as root (writes to /etc, /opt, /var/lib, and the systemd
# unit directory). If KILO_GATEWAY_API_KEY is set in the caller's
# environment, it's written to /etc/forge/forge.env with mode 0600
# so systemd can read it on start.

set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
    echo "ERROR: must run as root (writes to /etc, /opt, /var/lib)." >&2
    exit 1
fi

APP_SETTINGS="/etc/forge/appsettings.json"
RELEASE_DIR=""
UNIT_NAME="forge"
ENABLE=1
START_=1

while [[ $# -gt 0 ]]; do
    case "$1" in
        --app-settings) APP_SETTINGS="$2"; shift 2 ;;
        --release-dir)  RELEASE_DIR="$2"; shift 2 ;;
        --unit-name)    UNIT_NAME="$2"; shift 2 ;;
        --no-enable)    ENABLE=0; shift ;;
        --no-start)     START_=0; shift ;;
        -h|--help)
            sed -n '2,28p' "$0"
            exit 0 ;;
        *)
            echo "ERROR: unknown argument: $1" >&2
            exit 2 ;;
    esac
done

if [[ -z "$RELEASE_DIR" ]]; then
    echo "ERROR: --release-dir is required (the published Forge.Core dir)." >&2
    exit 2
fi

RELEASE_DIR="$(readlink -f "$RELEASE_DIR")"
if [[ ! -f "$RELEASE_DIR/Forge.Core.dll" ]]; then
    echo "ERROR: no Forge.Core.dll at $RELEASE_DIR — run 'dotnet publish Forge.Core.csproj -c Release -o <path>' first." >&2
    exit 1
fi

if [[ ! -f "$APP_SETTINGS" ]]; then
    mkdir -p "$(dirname "$APP_SETTINGS")"
    if [[ -f "$(dirname "$(readlink -f "$0")")/../appsettings.example.json" ]]; then
        cp "$(dirname "$(readlink -f "$0")")/../appsettings.example.json" "$APP_SETTINGS"
        echo "Seeded $APP_SETTINGS from appsettings.example.json — fill in real values before starting the service."
    else
        echo "WARN: no appsettings.example.json in the repo root; created empty $APP_SETTINGS." >&2
        : > "$APP_SETTINGS"
    fi
    chmod 0640 "$APP_SETTINGS"
fi

# Create the unprivileged user on first install.
if ! id -u forge >/dev/null 2>&1; then
    useradd --system \
        --home /var/lib/forge \
            --shell /usr/sbin/nologin \
            --comment "Forge orchestrator" \
            forge
    echo "Created system user 'forge' (home /var/lib/forge, shell nologin)."
fi

# Layout:
#   /opt/forge/releases/<sha>/Forge.Core.dll  (published release)
#   /opt/forge/current -> releases/<sha>      (symlink, what the unit runs)
#   /etc/forge/appsettings.json                (config)
#   /etc/forge/forge.env                       (optional secrets; mode 0600)
#   /var/lib/forge/                            (state dir: StateDirectory)
mkdir -p /opt/forge/releases /etc/forge /var/lib/forge
chown -R forge:forge /var/lib/forge
chmod 0750 /var/lib/forge /etc/forge

# Drop secrets into /etc/forge/forge.env if the caller provided them.
if [[ -n "${KILO_GATEWAY_API_KEY:-}" ]] || [[ -n "${GITHUB_TOKEN:-}" ]]; then
    SECRET_FILE="/etc/forge/forge.env"
    {
        [[ -n "${KILO_GATEWAY_API_KEY:-}" ]] && echo "KILO_GATEWAY_API_KEY=${KILO_GATEWAY_API_KEY}"
        [[ -n "${GITHUB_TOKEN:-}" ]] && echo "GITHUB_TOKEN=${GITHUB_TOKEN}"
    } > "$SECRET_FILE"
    chmod 0600 "$SECRET_FILE"
    chown root:forge "$SECRET_FILE"
    echo "Wrote secrets to $SECRET_FILE (mode 0600, root:forge)."
fi

# Repoint /opt/forge/current. Idempotent — `ln -sfn` replaces an
# existing symlink atomically.
ln -sfn "$RELEASE_DIR" /opt/forge/current
echo "Repointed /opt/forge/current -> $RELEASE_DIR"

# Install the unit file. We copy (not symlink) so operators can edit
# the unit locally with `systemctl edit forge` without touching the
# repo.
UNIT_SRC="$(dirname "$(readlink -f "$0")")/../deploy/systemd/${UNIT_NAME}.service"
if [[ ! -f "$UNIT_SRC" ]]; then
    echo "ERROR: unit template not found at $UNIT_SRC." >&2
    exit 1
fi
install -m 0644 "$UNIT_SRC" "/etc/systemd/system/${UNIT_NAME}.service"
echo "Installed /etc/systemd/system/${UNIT_NAME}.service"

systemctl daemon-reload

if [[ $ENABLE -eq 1 ]]; then
    systemctl enable "${UNIT_NAME}.service"
    echo "Enabled ${UNIT_NAME}.service."
fi

if [[ $START_ -eq 1 ]]; then
    systemctl restart "${UNIT_NAME}.service"
    echo "Restarted ${UNIT_NAME}.service."
    sleep 2
    systemctl status "${UNIT_NAME}.service" --no-pager || true
fi

cat <<EOF

Install complete.

  Unit:        ${UNIT_NAME}.service
  Binary:      /opt/forge/current -> $RELEASE_DIR
  Config:      $APP_SETTINGS
  Secrets:     /etc/forge/forge.env (if populated)
  State:       /var/lib/forge
  Dashboard:   http://127.0.0.1:4097 (after first start)

Operational commands:
  sudo systemctl status ${UNIT_NAME}        # liveness
  sudo journalctl -u ${UNIT_NAME} -f         # live log tail
  sudo systemctl restart ${UNIT_NAME}        # bounce (also used by SelfHostedSystemdService deploys)
  sudo scripts/uninstall-systemd-service.sh  # full uninstall

EOF