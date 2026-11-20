#!/bin/bash
: "${FORGE_BASE_URL:?Set FORGE_BASE_URL to your dashboard URL}"
while true; do
  sleep 30
  S=$(wget --no-check-certificate -qO- "${FORGE_BASE_URL%/}/api/specs/spec-9c44bcc6eddd439e8eb200e6cd7cf287" 2>/dev/null | python3 -c "import json,sys; print(json.load(sys.stdin).get('status'))" 2>/dev/null)
  echo "$(date +%H:%M:%S) status=$S"
  if [ -n "$S" ] && [ "$S" != "ReadyForDesign" ]; then
    echo "STATUS CHANGED -> $S"
    break
  fi
done
