#!/usr/bin/env bash
# refresh-sql-firewall.sh — keep the Azure SQL firewall aligned with this
# machine's (dynamic) egress IP, and keep the az CLI session warm so
# Active Directory Default token acquisition keeps working for the forge
# service.
#
# Runs as ExecStartPre on the forge service and every 15 min via
# forge-sql-firewall.timer. Idempotent; exits 0 even when offline so the
# service start is never blocked by a transient network blip (the DB
# retry layer rides out a stale rule until the next refresh).
set -u

SERVER="forge-sql-server"
RESOURCE_GROUP="forge"
RULE_NAME="forge-dev-machine"
DB_RESOURCE="https://database.windows.net"

log() { logger -t forge-sql-firewall -- "$1" 2>/dev/null || echo "$1"; }

IP="$(curl -fsS --max-time 10 https://ifconfig.me 2>/dev/null || true)"
if [ -z "$IP" ]; then
    log "could not resolve egress IP; leaving existing rule in place"
else
    CURRENT="$(az sql server firewall-rule show \
        --server "$SERVER" --resource-group "$RESOURCE_GROUP" --name "$RULE_NAME" \
        --query startIpAddress -o tsv 2>/dev/null || true)"
    if [ "$CURRENT" = "$IP" ]; then
        log "rule $RULE_NAME already current ($IP)"
    else
        if az sql server firewall-rule create \
            --server "$SERVER" --resource-group "$RESOURCE_GROUP" --name "$RULE_NAME" \
            --start-ip-address "$IP" --end-ip-address "$IP" -o none 2>/dev/null; then
            log "rule $RULE_NAME updated -> $IP"
        else
            log "firewall-rule update failed (az session expired? run: az login)"
        fi
    fi
fi

# Keepalive: forces a token refresh so AzureCliCredential in the forge
# process keeps succeeding between interactive uses.
if az account get-access-token --resource "$DB_RESOURCE" -o none 2>/dev/null; then
    log "az token for $DB_RESOURCE refreshed"
else
    log "az token refresh FAILED — DB auth will fail for new connections (run: az login)"
fi

exit 0
