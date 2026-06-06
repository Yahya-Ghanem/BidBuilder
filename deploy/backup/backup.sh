#!/bin/sh
# Periodic Postgres backup for BidBuilder, run inside the `db-backup` sidecar.
#
# Dumps the database with pg_dump, gzips it to $BACKUP_DIR with a UTC-timestamped
# name, then prunes dumps older than $BACKUP_RETENTION_DAYS. Safe to run on demand
# too: `docker compose -f docker-compose.prod.yml exec db-backup sh /scripts/backup.sh`.
set -eu

: "${PGHOST:=db}"
: "${PGPORT:=5432}"
: "${PGUSER:?PGUSER must be set}"
: "${PGPASSWORD:?PGPASSWORD must be set}"
: "${PGDATABASE:?PGDATABASE must be set}"
: "${BACKUP_DIR:=/backups}"
: "${BACKUP_RETENTION_DAYS:=14}"

export PGPASSWORD
mkdir -p "$BACKUP_DIR"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
out="$BACKUP_DIR/bidbuilder_${stamp}.sql.gz"
tmp="${out}.partial"

echo "[backup] $(date -u +%FT%TZ) dumping ${PGDATABASE}@${PGHOST} -> ${out}"
# Plain-SQL dump with --clean --if-exists so it restores cleanly onto an existing
# database. Write to a .partial file first and rename only on success, so a crashed
# dump never leaves a truncated file that looks like a valid backup.
pg_dump -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" --clean --if-exists \
  | gzip -c > "$tmp"
mv "$tmp" "$out"
echo "[backup] wrote $(du -h "$out" | cut -f1) ${out}"

# Retention: delete dumps older than N days (best-effort; never fail the run on this).
find "$BACKUP_DIR" -name 'bidbuilder_*.sql.gz' -type f -mtime "+${BACKUP_RETENTION_DAYS}" -print -delete 2>/dev/null || true

echo "[backup] current backups:"
ls -1t "$BACKUP_DIR"/bidbuilder_*.sql.gz 2>/dev/null | head -10 || true
