#!/usr/bin/env bash
# Stops and removes the Forge systemd service. Does NOT delete
# /opt/forge/releases, /var/lib/forge, /etc/forge, or the `forge`
# system user — only the unit + its enabled state. Re-running the
# install script can rebuild the unit without losing state.

set -euo pipefail

UNIT_NAME="${1:-forge}"

if [[ ${EUID} -ne 0 ]]; then
    echo "ERROR: must run as root." >&2
    exit 1
fi

UNIT_FILE="/etc/systemd/system/${UNIT_NAME}.service"
if [[ ! -f "$UNIT_FILE" ]]; then
    echo "Service unit ${UNIT_FILE} is not installed. Nothing to do."
    exit 0
fi

if systemctl is-active --quiet "${UNIT_NAME}.service"; then
    echo "Stopping ${UNIT_NAME}.service..."
    systemctl stop "${UNIT_NAME}.service"
    systemctl wait "${UNIT_NAME}.service" --timeout=30 || true
fi

if systemctl is-enabled --quiet "${UNIT_NAME}.service"; then
    echo "Disabling ${UNIT_NAME}.service..."
    systemctl disable "${UNIT_NAME}.service"
fi

echo "Removing ${UNIT_FILE}..."
rm -f "$UNIT_FILE"
systemctl daemon-reload

cat <<EOF

Uninstall complete.

Preserved (re-run install-systemd-service.sh to bring back the service):
  /opt/forge/releases/   (published releases)
  /opt/forge/current     (symlink; dangling if you removed the release)
  /var/lib/forge/        (SQLite state, JSONL mirror, worktrees)
  /etc/forge/            (appsettings.json + forge.env)
  forge system user

To remove the user + data too (irreversible):
  sudo userdel forge && sudo rm -rf /var/lib/forge /opt/forge /etc/forge

EOF