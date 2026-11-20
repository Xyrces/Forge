#!/bin/bash
: "${FORGE_BASE_URL:?Set FORGE_BASE_URL to your dashboard URL}"
while true; do
  sleep 30
  A=$(wget --no-check-certificate -qO- "${FORGE_BASE_URL%/}/api/state" 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); s=[x for x in d.get('sprints',[]) if x['status']=='Active']; print(s[0]['name']+'|'+str(s[0]['id']) if s else '')" 2>/dev/null)
  echo "$(date +%H:%M:%S) active=$A"
  if [ -n "$A" ]; then echo "SPRINT ACTIVE: $A"; break; fi
done
