#!/bin/bash
: "${FORGE_BASE_URL:?Set FORGE_BASE_URL to your dashboard URL}"
while true; do
  sleep 30
  S=$(wget --no-check-certificate -qO- "${FORGE_BASE_URL%/}/api/specs/spec-9c44bcc6eddd439e8eb200e6cd7cf287" 2>/dev/null | python3 -c "import json,sys; print(json.load(sys.stdin).get('status'))" 2>/dev/null)
  T=$(wget --no-check-certificate -qO- "${FORGE_BASE_URL%/}/api/specs/spec-9c44bcc6eddd439e8eb200e6cd7cf287/tree" 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(len(d.get('stories',[])))" 2>/dev/null)
  echo "$(date +%H:%M:%S) status=$S stories=$T"
  if [ "$S" = "Groomed" ]; then echo "GROOMED"; break; fi
done
