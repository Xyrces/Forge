#!/usr/bin/env bash
# One-time setup: install a NOPASSWD sudoers drop-in that lets the
# forge install / publish flow re-apply CAP_NET_BIND_SERVICE on the
# .NET runtime without prompting. Required for the dashboard to
# bind 80/443.
#
# Run this ONCE as the user that owns the forge service (not as
# root). You'll be prompted for the sudo password exactly once;
# after that, every subsequent setcap call (post-publish, restart,
# reinstall) is non-interactive.
#
# Reversal: `sudo rm /etc/sudoers.d/forge-setcap` revokes the rule.
#
# Idempotent: re-running is safe; the drop-in is overwritten in
# place and the cap is re-applied. Use `--check` to verify the rule
# is in place + the cap is currently set, no writes.
#
# Exit codes:
#   0  setup is in place (either already was, or this run applied it)
#   1  sudo not available / sudoers write failed
#   2  setcap failed (no rule or dotnet binary missing)

set -euo pipefail

DOTNET="${DOTNET_BIN:-/home/jtn5016/.dotnet/dotnet}"
SUDOERS_FILE="/etc/sudoers.d/forge-setcap"
SUDOERS_RULE="jtn5016 ALL=(ALL) NOPASSWD: /sbin/setcap cap_net_bind_service=+ep ${DOTNET}"

if [[ "${1:-}" == "--check" ]]; then
    if [[ ! -f "$SUDOERS_FILE" ]]; then
        echo "MISSING: $SUDOERS_FILE does not exist" >&2
        exit 1
    fi
    CAP=$(getcap "$DOTNET" 2>/dev/null || true)
    if [[ "$CAP" != *"cap_net_bind_service"* ]]; then
        echo "MISSING: $DOTNET lacks CAP_NET_BIND_SERVICE (current: $CAP)" >&2
        exit 2
    fi
    echo "OK: $SUDOERS_FILE + $CAP"
    exit 0
fi

# Apply the sudoers drop-in. sudo prompts once; after this the
# NOPASSWD rule is in effect and the remaining setcap calls are
# silent.
echo "$SUDOERS_RULE" | sudo tee "$SUDOERS_FILE" >/dev/null
sudo chmod 0440 "$SUDOERS_FILE"

# Apply the cap now (sudoers rule is in effect, so this is silent).
sudo setcap cap_net_bind_service=+ep "$DOTNET"

# Verify.
getcap "$DOTNET" | grep -q "cap_net_bind_service" || {
    echo "ERROR: setcap did not stick — $DOTNET still has no CAP_NET_BIND_SERVICE" >&2
    exit 2
}

cat <<EOF

forge cap setup complete.

  sudoers rule : $SUDOERS_FILE (NOPASSWD for the specific setcap call)
  cap granted  : $(getcap "$DOTNET")
  dotnet binary: $DOTNET

From now on, post-publish / restart flows can call:
    sudo -n /sbin/setcap cap_net_bind_service=+ep $DOTNET
non-interactively. The forge user-mode unit's ExecStartPre
already does this; system-mode install-systemd-service.sh
also re-applies the cap as part of its publish step.

To verify at any time: scripts/forge-setcap-setup.sh --check

EOF
