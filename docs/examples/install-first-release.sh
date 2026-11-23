#!/usr/bin/env bash
# Run the first-install example against the installer from the selected image build.
set -euo pipefail
if [[ $# -lt 2 || $# -gt 3 || ! $2 =~ ^[0-9]+$ || ( $# -eq 3 && $3 != --apply ) ]]; then
  echo "Usage: bash $0 SETUP_DIRECTORY WORKFLOW_RUN_ID [--apply]" >&2
  exit 2
fi
setup=$1
release="$setup/releases/$2"
inputs="$setup/inputs"
receipt="$release/forge-image-receipt.json"
source_sha=$(git -C "$release/source" rev-parse HEAD)
if [[ $(jq -er .workflowRunId "$receipt") != "$2" ]]; then
  echo "The receipt belongs to a different workflow run." >&2
  exit 1
fi
args=()
if [[ -d "$setup/github-apps" ]]; then
  args+=(--github-apps-directory "$setup/github-apps")
fi
if [[ $# -eq 3 ]]; then args+=(--apply); fi
exec python3 "$release/source/deploy/k3s/install.py" \
  --image "$(jq -er .imageDigest "$receipt")" --image-receipt "$receipt" \
  --source-sha "$source_sha" --dataset "$(cat "$inputs/dataset-id")" \
  --operation first-install --config "$inputs/appsettings.json" \
  --sql-file "$inputs/sql-controller.txt" --initialize-sql-file "$inputs/sql-initialize.txt" \
  --worker-sql-file "$inputs/sql-worker.txt" --worker-packages-file "$inputs/worker-packages.json" \
  --registry-config "$inputs/registry.json" "${args[@]}" \
  --k3s-version 'v1.37.2+k3s1' \
  --k3s-sha256 '265b3ee6a3198ef14033c63ef46cbb32370d83795199fb6d1b01f21575eddbda'
