#!/bin/bash
: "${FORGE_BASE_URL:?Set FORGE_BASE_URL to your dashboard URL}"
: "${FORGE_TASK_ID:?Set FORGE_TASK_ID to the task to watch}"
while true; do
  sleep 60
  P=$(wget --no-check-certificate -qO- "${FORGE_BASE_URL%/}/api/tasks/${FORGE_TASK_ID}?projectId=forge" 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(str(d['status'])+'|'+str(d['dispatchCheckpoint'])+'|'+str(d['metadata'].get('prNumber')))" 2>/dev/null)
  echo "$(date +%H:%M:%S) ${FORGE_TASK_ID}: $P"
  case "$P" in *PrOpened*|*"|Completed"*) echo "PR PHASE REACHED: $P"; break;; esac
done
