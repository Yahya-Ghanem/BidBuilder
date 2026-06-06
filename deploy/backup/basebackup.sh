#!/bin/sh
# 19.4 — Periodic Postgres physical base backup for PITR. Runs inside the
# `db-backup` sidecar (which has the postgres-client tools and the WAL archive
# volume mounted). Each successful run produces a self-contained tarball under
# /backups/base/<stamp>/ that, combined with the WAL archive at /var/lib/postgresql/archive,
# is enough to restore the cluster to any point in time after the backup.
#
# Why both this AND backup.sh? backup.sh takes a logical pg_dump — easy to
# inspect, easy to import into a different Postgres major version. PITR needs a
# PHYSICAL backup (file-level cluster snapshot) plus the WAL stream. They're
# complementary, not redundant.
set -eu

: "${PGHOST:=db}"
: "${PGPORT:=5432}"
: "${PGUSER:?PGUSER must be set}"
: "${PGPASSWORD:?PGPASSWORD must be set}"
: "${BACKUP_DIR:=/backups}"
: "${WAL_RETENTION_DAYS:=21}"
: "${ARCHIVE_DIR:=/var/lib/postgresql/archive}"

export PGPASSWORD

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
base_dir="${BACKUP_DIR}/base/${stamp}"
tmp_dir="${base_dir}.partial"

echo "[basebackup] $(date -u +%FT%TZ) starting -> ${base_dir}"
mkdir -p "$tmp_dir"

# pg_basebackup with -X stream uses a second WAL-sender connection to ship the
# WAL needed to reach a consistent point alongside the data files — so the
# tarball is self-contained even if the archive process is briefly stalled.
# -Ft -z packs the cluster + pg_wal into gzipped tarballs (base.tar.gz +
# pg_wal.tar.gz). --checkpoint=fast triggers an immediate checkpoint so the
# backup window is as short as possible.
if ! pg_basebackup \
      -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" \
      -D "$tmp_dir" \
      -Ft -z \
      -X stream \
      --checkpoint=fast \
      --label="bidbuilder-${stamp}" \
      --no-password \
      --verbose ; then
    echo "[basebackup] FAILED — leaving ${tmp_dir} for inspection"
    exit 1
fi

# Atomic rename only on success so a crashed run never leaves a half-written
# directory that looks like a valid restore point.
mv "$tmp_dir" "$base_dir"
echo "[basebackup] wrote $(du -sh "$base_dir" | cut -f1) ${base_dir}"

# WAL retention. WAL older than WAL_RETENTION_DAYS is safe to drop AS LONG AS at
# least one base backup older than the cutoff still exists — otherwise we'd
# stand orphan WAL we can never replay (no anchor) AND lose the ability to
# restore to that window. So: only prune WAL if a current base backup is younger
# than the cutoff. Belt + braces.
younger_base="$(find "${BACKUP_DIR}/base" -maxdepth 1 -mindepth 1 -type d -mtime "-${WAL_RETENTION_DAYS}" 2>/dev/null | head -1 || true)"
if [ -n "$younger_base" ]; then
    pruned="$(find "$ARCHIVE_DIR" -type f -mtime "+${WAL_RETENTION_DAYS}" -print -delete 2>/dev/null | wc -l)"
    echo "[basebackup] WAL pruned: ${pruned} segment(s) older than ${WAL_RETENTION_DAYS}d"
else
    echo "[basebackup] WAL prune SKIPPED — no base backup younger than ${WAL_RETENTION_DAYS}d (refusing to orphan WAL)"
fi

# Restore-point catalogue: a human-readable list at the volume root so an
# operator can see at a glance what they can restore to.
{
    echo "# BidBuilder base-backup catalogue — generated $(date -u +%FT%TZ)";
    echo "# format: <stamp> <size> <path>";
    find "${BACKUP_DIR}/base" -maxdepth 1 -mindepth 1 -type d \
        | sort \
        | while read -r d; do
            printf '%s\t%s\t%s\n' "$(basename "$d")" "$(du -sh "$d" | cut -f1)" "$d"
        done
} > "${BACKUP_DIR}/base/INDEX.txt"

echo "[basebackup] catalogue updated -> ${BACKUP_DIR}/base/INDEX.txt"
