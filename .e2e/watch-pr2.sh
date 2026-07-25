#!/bin/bash
while true; do
  sleep 60
  L=$(~/.local/bin/gh pr list --repo Xyrces/Forge --state open --limit 5 --json number,title 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print('; '.join(f\"#{p['number']} {p['title'][:50]}\" for p in d))" 2>/dev/null)
  T=$(wget --no-check-certificate -qO- "https://192.168.68.78/api/tasks/task-153?projectId=forge" 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['status'])" 2>/dev/null)
  echo "$(date +%H:%M:%S) task-153=$T open_prs=[$L]"
  if [ -n "$L" ]; then echo "PR OPEN: $L"; break; fi
done
