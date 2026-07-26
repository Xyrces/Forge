#!/bin/bash
while true; do
  sleep 60
  P=$(wget --no-check-certificate -qO- "https://192.168.68.78/api/tasks/task-153?projectId=forge" 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(str(d['status'])+'|'+str(d['dispatchCheckpoint'])+'|'+str(d['metadata'].get('prNumber')))" 2>/dev/null)
  echo "$(date +%H:%M:%S) task-153: $P"
  case "$P" in *PrOpened*|*"|Completed"*) echo "PR PHASE REACHED: $P"; break;; esac
done
