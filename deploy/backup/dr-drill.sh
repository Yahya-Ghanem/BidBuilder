#!/bin/sh
# 19.4 — Disaster-recovery drill. Proves end-to-end that the PITR pipeline
# (WAL archive + base backup + restore) actually works, WITHOUT touching the
# live cluster.
#
# What it does:
#   1. Pick the most recent base backup under /backups/base.
#   2. Restore it into a throwaway dir (/tmp/dr-drill).
#   3. Start a SECOND postgres process on a temporary port (5433) against that
#      dir, with restore_command pointed at the live WAL archive.
#   4. Wait for recovery to reach a consistent state.
#   5. Run a validation query — count rows in a marker table the live API
#      maintains, then dump the schema list — to prove data made it through.
#   6. Tear down the temporary postgres + restore dir.
#
# Exit codes: 0 success / non-zero failure. Run from inside the db-backup
# sidecar (it has pg_ctl + initdb + psql). Schedule weekly via cron OR run on
# demand:
#
#   docker compose -f docker-compose.prod.yml exec db-backup sh /scripts/dr-drill.sh
set -eu

: "${PGUSER:?PGUSER must be set}"
: "${PGPASSWORD:?PGPASSWORD must be set}"
: "${PGDATABASE:?PGDATABASE must be set}"
: "${ARCHIVE_DIR:=/var/lib/postgresql/archive}"
: "${BASE_DIR:=/backups/base}"

DRILL_DIR="${DRILL_DIR:-/tmp/dr-drill}"
DRILL_PORT="${DRILL_PORT:-5433}"
DRILL_LOG="${DRILL_DIR}.log"

export PGPASSWORD

# Latest base backup wins. If there's none, exit 2 — that means the basebackup
# loop hasn't run yet, which is itself an actionable failure.
latest="$(ls -1 "$BASE_DIR" 2>/dev/null | grep -E '^[0-9]{8}T[0-9]{6}Z$' | sort | tail -1)"
if [ -z "$latest" ]; then
    echo "[drill] FAIL — no base backups under ${BASE_DIR}"
    exit 2
fi
echo "[drill] $(date -u +%FT%TZ) selected base ${latest}"

# Clean slate. The script owns DRILL_DIR — never run with DRILL_DIR pointing at
# anything you want to keep.
rm -rf "$DRILL_DIR"
mkdir -p "$DRILL_DIR"
tar -xzf "${BASE_DIR}/${latest}/base.tar.gz" -C "$DRILL_DIR"
if [ -f "${BASE_DIR}/${latest}/pg_wal.tar.gz" ]; then
    mkdir -p "${DRILL_DIR}/pg_wal"
    tar -xzf "${BASE_DIR}/${latest}/pg_wal.tar.gz" -C "${DRILL_DIR}/pg_wal"
fi
# The sidecar runs as root, but pg_ctl will refuse to start as root and the
# postgres user needs to own PGDATA. Chown defensively — a no-op if we're already
# postgres-owned (e.g. when the sidecar drops privileges).
chown -R postgres:postgres "$DRILL_DIR" 2>/dev/null || true

# Recovery config — point at the live WAL archive (read-only is fine here, the
# drill never writes back). recovery_target_time omitted → replay everything.
{
    echo "port = ${DRILL_PORT}"
    echo "unix_socket_directories = '${DRILL_DIR}'"
    echo "restore_command = 'cp ${ARCHIVE_DIR}/%f %p'"
    echo "archive_mode = off"
} >> "${DRILL_DIR}/postgresql.auto.conf"
touch "${DRILL_DIR}/recovery.signal"
chmod 700 "$DRILL_DIR"

# Spawn the throwaway postgres. pg_ctl handles fork + pid file. Logs to
# DRILL_LOG so a failed start leaves something to grep. We're typically running
# as root in the sidecar, but pg_ctl refuses to launch as root — su to postgres.
echo "[drill] starting throwaway postgres on port ${DRILL_PORT}"
if [ "$(id -u)" = "0" ]; then
    chown postgres:postgres "$DRILL_LOG" 2>/dev/null || true
    if ! su postgres -c "pg_ctl -D '$DRILL_DIR' -l '$DRILL_LOG' -o '-p ${DRILL_PORT}' -w start" ; then
        echo "[drill] FAIL — postgres did not start. Log tail:"
        tail -50 "$DRILL_LOG" || true
        exit 1
    fi
else
    if ! pg_ctl -D "$DRILL_DIR" -l "$DRILL_LOG" -o "-p ${DRILL_PORT}" -w start ; then
        echo "[drill] FAIL — postgres did not start. Log tail:"
        tail -50 "$DRILL_LOG" || true
        exit 1
    fi
fi

cleanup() {
    if [ "$(id -u)" = "0" ]; then
        su postgres -c "pg_ctl -D '$DRILL_DIR' stop -m fast" >/dev/null 2>&1 || true
    else
        pg_ctl -D "$DRILL_DIR" stop -m fast >/dev/null 2>&1 || true
    fi
    rm -rf "$DRILL_DIR" "$DRILL_LOG"
}
trap cleanup EXIT INT TERM

# Wait for recovery to finish (pg_is_in_recovery() returns false).
echo "[drill] waiting for recovery to reach a consistent state..."
i=0
while [ "$i" -lt 60 ]; do
    state="$(psql -h "$DRILL_DIR" -p "$DRILL_PORT" -U "$PGUSER" -d "$PGDATABASE" -tA -c "SELECT pg_is_in_recovery()" 2>/dev/null || echo "?")"
    case "$state" in
        f|"false") echo "[drill] recovery complete"; break ;;
        t|"true") sleep 2; i=$((i+1)) ;;
        *)        sleep 2; i=$((i+1)) ;;
    esac
done
if [ "$i" -ge 60 ]; then
    echo "[drill] FAIL — recovery did not complete within 120s. Log tail:"
    tail -50 "$DRILL_LOG" || true
    exit 1
fi

# Validation. Count Tenants + Users + Projects + Estimates — every BidBuilder
# tenant has at least one of each (seed + smoke flows), so a 0 here means the
# WAL replay actually dropped data on the floor.
echo "[drill] validating recovered cluster..."
counts="$(psql -h "$DRILL_DIR" -p "$DRILL_PORT" -U "$PGUSER" -d "$PGDATABASE" -tA -F'|' -c "
    SELECT
        (SELECT COUNT(*) FROM \"Tenants\")::text,
        (SELECT COUNT(*) FROM \"Users\")::text,
        (SELECT COUNT(*) FROM \"Projects\")::text,
        (SELECT COUNT(*) FROM \"Estimates\")::text
" 2>/dev/null || echo "?|?|?|?")"

tenants="$(echo "$counts" | cut -d'|' -f1)"
users="$(echo   "$counts" | cut -d'|' -f2)"
projects="$(echo "$counts" | cut -d'|' -f3)"
estimates="$(echo "$counts" | cut -d'|' -f4)"

echo "[drill] recovered counts: tenants=${tenants} users=${users} projects=${projects} estimates=${estimates}"

# A drill PASSES if (a) we got a numeric answer and (b) at least one tenant exists.
# A fresh tenant with zero projects/estimates is still a valid recovery — just no
# user data yet — so we don't fail on projects=0 in production. But tenants must
# exist (the seed creates "default") or something dropped the system catalog.
case "$tenants" in
    ''|'?'|0)
        echo "[drill] FAIL — recovered cluster has no tenants. WAL replay broken."
        exit 1
        ;;
esac

echo "[drill] PASS — PITR pipeline verified at $(date -u +%FT%TZ)"
