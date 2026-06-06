#!/bin/sh
# Restore a BidBuilder Postgres backup produced by backup.sh.
#
# Stop the app first so nothing writes mid-restore, then run this in the sidecar:
#
#   docker compose -f docker-compose.prod.yml stop api web
#   docker compose -f docker-compose.prod.yml run --rm --entrypoint sh db-backup \
#     /scripts/restore.sh /backups/bidbuilder_YYYYMMDDTHHMMSSZ.sql.gz
#   docker compose -f docker-compose.prod.yml start api web
#
# The db-backup service already carries PGHOST/PGUSER/PGPASSWORD/PGDATABASE, so
# `run --rm` inherits them.
set -eu

: "${PGHOST:=db}"
: "${PGPORT:=5432}"
: "${PGUSER:?PGUSER must be set}"
: "${PGPASSWORD:?PGPASSWORD must be set}"
: "${PGDATABASE:?PGDATABASE must be set}"
export PGPASSWORD

file="${1:?usage: restore.sh <path-to-backup.sql.gz>}"
[ -f "$file" ] || { echo "[restore] no such file: $file" >&2; exit 1; }

echo "[restore] $(date -u +%FT%TZ) restoring ${file} -> ${PGDATABASE}@${PGHOST}"
echo "[restore] WARNING: this overwrites the current contents of ${PGDATABASE}."
# ON_ERROR_STOP so a failed statement aborts the restore instead of leaving the
# database half-applied. The dump's --clean --if-exists drops existing objects first.
gunzip -c "$file" | psql -v ON_ERROR_STOP=1 -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE"
echo "[restore] done. Start the api/web services again if they were stopped."
