#!/bin/bash
: "${FORGE_BASE_URL:?Set FORGE_BASE_URL to your dashboard URL}"
: "${FORGE_SPEC_ID:?Set FORGE_SPEC_ID to the spec to watch}"
while true; do
  sleep 30
  S=$(wget --no-check-certificate -qO- "${FORGE_BASE_URL%/}/api/specs/${FORGE_SPEC_ID}" 2>/dev/null | python3 -c "import json,sys; print(json.load(sys.stdin).get('status'))" 2>/dev/null)
  echo "$(date +%H:%M:%S) status=$S"
  if [ -n "$S" ] && [ "$S" != "ReadyForDesign" ]; then
    echo "STATUS CHANGED -> $S"
    break
  fi
done
