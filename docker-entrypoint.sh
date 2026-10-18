#!/bin/sh
# Railway mounts volumes owned by root, which the non-root `app` user cannot write to.
# When the platform starts us as root, fix ownership of the persistent upload paths and
# drop to `app`; otherwise exec straight into the application.
set -e

UPLOAD_DIRS="/app/wwwroot/uploads /app/wwwroot/qrcodes"

# Railway assigns the listening port dynamically; honour it when present.
if [ -n "$PORT" ]; then
    export ASPNETCORE_URLS="http://+:${PORT}"
fi

if [ "$(id -u)" = "0" ]; then
    for dir in $UPLOAD_DIRS; do
        mkdir -p "$dir"
        # The platform mounts volumes as root; the app runs as `app`, so hand the
        # mount point over. Files created inside are already owned by `app`.
        chown app:app "$dir"
    done
    exec setpriv --reuid=app --regid=app --init-groups "$@"
fi

exec "$@"
