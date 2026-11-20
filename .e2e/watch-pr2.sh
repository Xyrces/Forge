#!/bin/bash
: "${FORGE_BASE_URL:?Set FORGE_BASE_URL to your dashboard URL}"
: "${FORGE_TASK_ID:?Set FORGE_TASK_ID to the task to watch}"
while true; do
  sleep 60
  L=$(~/.local/bin/gh pr list --repo Xyrces/Forge --state open --limit 5 --json number,title 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print('; '.join(f\"#{p['number']} {p['title'][:50]}\" for p in d))" 2>/dev/null)
  T=$(wget --no-check-certificate -qO- "${FORGE_BASE_URL%/}/api/tasks/${FORGE_TASK_ID}?projectId=forge" 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['status'])" 2>/dev/null)
  echo "$(date +%H:%M:%S) ${FORGE_TASK_ID}=$T open_prs=[$L]"
  if [ -n "$L" ]; then echo "PR OPEN: $L"; break; fi
done
