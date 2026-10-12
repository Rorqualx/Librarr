#!/bin/sh
# Librarr container entrypoint — shared by Dockerfile and Dockerfile.prebuilt.
#
# Started as root (the default): resolve PUID/PGID (default 1000:1000),
# make /config belong to them, apply the umask, then drop privileges with
# su-exec and exec the app. Nothing is left running as root.
#
# Started as non-root (`docker run --user 1000:1000`, Kubernetes
# runAsUser, ...): there is nothing to drop and no right to chown, so
# PUID/PGID are ignored — apply the umask and exec the app as-is.
#
# Every path ends in `exec`, so the app replaces this shell as PID 1 and
# receives SIGTERM from `docker stop` directly.
#
# POSIX sh on purpose (busybox ash on Alpine) — no bashisms.

set -eu

APP_BIN="${LIBRARR_HOME:-/app}/Readarr"
# Deliberately not configurable: this is the one path that gets chowned
# recursively, so it must never be pointed at a library.
CONFIG_DIR=/config
RUN_USER=librarr

log()  { echo "[entrypoint] $*"; }
warn() { echo "[entrypoint] WARNING: $*" >&2; }
die()  { echo "[entrypoint] ERROR: $*" >&2; exit 1; }

is_number() {
    case "$1" in
        ''|*[!0-9]*) return 1 ;;
        *) return 0 ;;
    esac
}

# `docker run image -data=/elsewhere` used to work because the entrypoint
# was the binary itself. Keep that: bare flags (or no arguments at all) go
# to the app; anything else is run as given (`docker run image sh`).
if [ "$#" -eq 0 ] || [ "${1#-}" != "$1" ]; then
    set -- "$APP_BIN" "$@"
fi

# UMASK_SET is the documented name; UMASK is accepted because several
# other self-hosted images use it.
umask_value="${UMASK_SET:-${UMASK:-022}}"
case "$umask_value" in
    ''|*[!0-7]*) die "umask '$umask_value' is not octal (set UMASK_SET to e.g. 022 or 002)" ;;
esac
[ "${#umask_value}" -le 4 ] || die "umask '$umask_value' is too long (expected e.g. 022 or 002)"
umask "$umask_value"

# ── Started as non-root ─────────────────────────────────────────────
if [ "$(id -u)" -ne 0 ]; then
    if [ -n "${PUID:-}${PGID:-}" ]; then
        log "running as $(id -u):$(id -g) (set by --user); PUID/PGID are ignored"
    fi
    if [ -d "$CONFIG_DIR" ] && [ ! -w "$CONFIG_DIR" ]; then
        warn "$CONFIG_DIR is not writable by $(id -u):$(id -g) — Librarr will not be able to start. Fix the ownership of the mounted directory on the host."
    fi
    for dir in /books /downloads; do
        if [ -d "$dir" ] && [ ! -w "$dir" ]; then
            warn "$dir is not writable by $(id -u):$(id -g) (ignore this if you mount your library/downloads somewhere else)"
        fi
    done
    # An arbitrary uid usually has no passwd entry, which leaves HOME
    # unset or pointing at `/`.
    if [ -z "${HOME:-}" ] || [ ! -w "$HOME" ]; then
        export HOME="$CONFIG_DIR"
    fi
    exec "$@"
fi

# ── Started as root ─────────────────────────────────────────────────
PUID="${PUID:-1000}"
PGID="${PGID:-1000}"
is_number "$PUID" || die "PUID '$PUID' is not a number"
is_number "$PGID" || die "PGID '$PGID' is not a number"

if [ "$PUID" -eq 0 ]; then
    warn "PUID=0 — running Librarr as root, as explicitly requested"
    exec "$@"
fi

# Give the ids a passwd/group entry so name lookups work inside the
# container. Written directly rather than via busybox adduser, which
# rejects ids above 256000 (common with user-namespace remapping).
# Not fatal: su-exec below takes numeric ids, so a read-only root
# filesystem only costs the names.
ensure_names() {
    if ! awk -F: -v id="$PGID" '$3 == id { found = 1 } END { exit !found }' /etc/group; then
        if awk -F: -v name="$RUN_USER" '$1 == name { found = 1 } END { exit !found }' /etc/group; then
            return 1
        fi
        echo "$RUN_USER:x:$PGID:" >> /etc/group || return 1
    fi
    if ! awk -F: -v id="$PUID" '$3 == id { found = 1 } END { exit !found }' /etc/passwd; then
        if awk -F: -v name="$RUN_USER" '$1 == name { found = 1 } END { exit !found }' /etc/passwd; then
            return 1
        fi
        echo "$RUN_USER:x:$PUID:$PGID:Librarr:$CONFIG_DIR:/sbin/nologin" >> /etc/passwd || return 1
    fi
    return 0
}
ensure_names 2>/dev/null || warn "could not add passwd/group entries for $PUID:$PGID (read-only root filesystem?); continuing with numeric ids"

# /config must be writable by the app, including files an earlier
# root-running container left behind. Only entries with the wrong owner
# are touched, so this is a cheap tree walk on every later start.
# /books and /downloads are never chowned — they can be enormous and
# their ownership is the host's business.
if [ -d "$CONFIG_DIR" ]; then
    if [ "$(stat -c '%u:%g' "$CONFIG_DIR" 2>/dev/null || true)" != "$PUID:$PGID" ]; then
        log "taking ownership of $CONFIG_DIR for $PUID:$PGID"
    fi
    find "$CONFIG_DIR" \( ! -user "$PUID" -o ! -group "$PGID" \) -exec chown -h "$PUID:$PGID" {} + 2>/dev/null \
        || warn "could not change ownership of everything under $CONFIG_DIR (read-only or root-squashed mount?)"
    su-exec "$PUID:$PGID" test -w "$CONFIG_DIR" \
        || warn "$CONFIG_DIR is not writable by $PUID:$PGID — Librarr will not be able to start. Fix the ownership of the mounted directory on the host, or set PUID/PGID to its owner."
fi

for dir in /books /downloads; do
    if [ -d "$dir" ] && ! su-exec "$PUID:$PGID" test -w "$dir"; then
        warn "$dir is not writable by $PUID:$PGID (ignore this if you mount your library/downloads somewhere else)"
    fi
done

log "starting Librarr as $PUID:$PGID, umask $umask_value"
exec su-exec "$PUID:$PGID" env HOME="$CONFIG_DIR" "$@"
