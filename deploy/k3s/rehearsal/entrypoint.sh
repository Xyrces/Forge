#!/bin/sh
# Dedicated qualification image only. Never merge this entrypoint into production.
set -eu
if [ "${Controller__Mode:-}" = "initialize" ]; then
    printf '%s\n' 'FORGE_REHEARSAL_INITIALIZER_WAIT_60_SECONDS'
    sleep 60
    printf '%s\n' 'FORGE_REHEARSAL_INITIALIZER_EXECUTING_UNCHANGED_BINARY'
    exec dotnet /app/Forge.Core.dll "$@"
fi
printf '%s\n' 'FORGE_REHEARSAL_CONTROLLER_INTENTIONALLY_UNAVAILABLE' >&2
exit 78
