# Backup & restore

The bid data **is** the product, so the production stack ships a two-layer
backup story: nightly **logical dumps** for clean import/export, and
continuous **WAL archiving + base backups** for point-in-time recovery (PITR).
This is the operational runbook.

## RPO / RTO at a glance

| Loss scenario | Recovery via | Target RPO | Target RTO |
|---|---|---|---|
| Application bug / accidental data wipe | PITR to just before the bad write | ≤ 5 min | ≤ 30 min |
| Postgres process crash, data files intact | Postgres internal WAL replay (automatic on restart) | 0 | ≤ 5 min |
| Single disk loss, host intact | Restore latest logical dump OR base backup + WAL | ≤ 24 h (dump) / ≤ 5 min (PITR) | ≤ 1 h |
| Whole-host loss | Off-host copy of `dbbackups` + `dbarchive` volumes → new host | ≤ off-host-sync interval | ≤ 4 h |

The PITR RPO is bounded by `archive_timeout=300` (the db service forces a WAL
switch every 5 minutes even if traffic is quiet), so the worst case is the last
5 minutes of writes on a low-traffic deployment. Under load, the WAL ships per
segment fill so the practical RPO is shorter.

## What runs automatically

`docker-compose.prod.yml` includes a **`db-backup`** service that:

- waits for Postgres to be healthy, then takes a gzipped `pg_dump` immediately and
  every `BACKUP_INTERVAL_SECONDS` thereafter (default **86400** = daily);
- writes timestamped files `bidbuilder_YYYYMMDDTHHMMSSZ.sql.gz` into the
  **`dbbackups`** named volume (`/backups` inside the container);
- prunes dumps older than `BACKUP_RETENTION_DAYS` (default **14**).

Tunable via env (in `.env` or your secret store):

| Variable | Default | Meaning |
|---|---|---|
| `BACKUP_INTERVAL_SECONDS` | `86400` | seconds between dumps |
| `BACKUP_RETENTION_DAYS` | `14` | delete dumps older than this |

## ⚠️ Off-host durability

A named volume on the same host protects against an accidental DB wipe or a bad
migration, **not** against losing the host. For real disaster recovery, get the
dumps off the machine. Either:

- **Bind-mount a host path** instead of the named volume, and back that directory
  up with your existing infrastructure — change the sidecar's volume line to
  `- /srv/bidbuilder-backups:/backups`; or
- **Sync to object storage** on a schedule (S3/GCS/Backblaze), e.g. a cron job on
  the host running `aws s3 sync` against the volume's mountpoint.

## Common operations

**List backups**
```sh
docker compose -f docker-compose.prod.yml exec db-backup ls -1t /backups
```

**Take an immediate backup** (outside the schedule)
```sh
docker compose -f docker-compose.prod.yml exec db-backup sh /scripts/backup.sh
```

**Copy a backup to the host**
```sh
docker cp bidbuilder-db-backup:/backups/bidbuilder_YYYYMMDDTHHMMSSZ.sql.gz ./
```

## Restore

A restore **overwrites** the current database. Stop the app first so nothing writes
mid-restore:

```sh
docker compose -f docker-compose.prod.yml stop api web

docker compose -f docker-compose.prod.yml run --rm --entrypoint sh db-backup \
  /scripts/restore.sh /backups/bidbuilder_YYYYMMDDTHHMMSSZ.sql.gz

docker compose -f docker-compose.prod.yml start api web
```

The dump is `--clean --if-exists`, so it drops and recreates objects; `restore.sh`
runs `psql` with `ON_ERROR_STOP=1` so a failed statement aborts instead of leaving a
half-applied database. On API start, `DbInitializer` re-checks migrations (a no-op if
the dump is already at the current schema).

## Test your restore

A backup you've never restored is a hope, not a backup. Periodically restore the
latest dump into a throwaway database and smoke-test it:

```sh
docker compose -f docker-compose.prod.yml exec db \
  psql -U "$POSTGRES_USER" -c 'CREATE DATABASE restore_test;'

docker compose -f docker-compose.prod.yml run --rm \
  --entrypoint sh -e PGDATABASE=restore_test db-backup \
  /scripts/restore.sh /backups/bidbuilder_YYYYMMDDTHHMMSSZ.sql.gz

# ... inspect restore_test, then drop it ...
docker compose -f docker-compose.prod.yml exec db \
  psql -U "$POSTGRES_USER" -c 'DROP DATABASE restore_test;'
```

## PITR (point-in-time recovery)

The prod db service runs with **continuous WAL archiving** enabled:

- `wal_level=replica`, `archive_mode=on`
- `archive_command` writes each completed WAL segment to the `dbarchive`
  named volume (`/var/lib/postgresql/archive` inside the container). The
  command refuses to overwrite, so a duplicate-segment bug becomes loud, not
  silent.
- `archive_timeout=300` forces a WAL switch every 5 minutes — bounds the RPO
  when traffic is quiet.

The `db-backup` sidecar takes **base backups** every `BASEBACKUP_INTERVAL_SECONDS`
(default weekly) via `pg_basebackup -Ft -z -X stream`. Each one lands under
`/backups/base/<stamp>/` and is enough — combined with the WAL archive — to
restore to any point in time after that backup was taken.

WAL retention: segments older than `WAL_RETENTION_DAYS` (default 21) are pruned,
but ONLY if at least one base backup younger than the cutoff still exists. This
makes orphaned WAL impossible: we never delete WAL that would have to anchor on
a base backup we've already discarded.

### Run an immediate base backup

```sh
docker compose -f docker-compose.prod.yml exec db-backup sh /scripts/basebackup.sh
```

### Restore the cluster to a point in time

```sh
# 1. List available restore points
docker compose -f docker-compose.prod.yml exec db-backup ls -1 /backups/base
# 2. Stage a recovery dir (does NOT touch the live cluster)
docker compose -f docker-compose.prod.yml exec db-backup \
  sh /scripts/pitr-restore.sh <stamp> '2026-06-06 14:32:00 UTC'
# 3. Follow the printed cut-over steps to swap /var/lib/postgresql/data
```

If you omit the timestamp, the script replays ALL archived WAL and stops at
the end of the stream (effectively the latest committed transaction Postgres
managed to archive). With a timestamp, recovery pauses at that point —
inspect, then `SELECT pg_wal_replay_resume();` and promote.

### Automated DR drill

`deploy/backup/dr-drill.sh` proves end-to-end that the PITR pipeline works
WITHOUT touching the live cluster: it picks the latest base backup, restores
into `/tmp/dr-drill`, starts a throwaway postgres on port 5433, waits for WAL
replay to finish, validates against the live `Tenants` / `Users` / `Projects`
/ `Estimates` tables, then tears the throwaway down. Run on-demand or wire
into your scheduler:

```sh
docker compose -f docker-compose.prod.yml exec db-backup sh /scripts/dr-drill.sh
```

Failure modes the drill catches: no base backups at all (exit 2), `pg_basebackup`
output that can't be untarred, broken `archive_command` (WAL segments missing
when replay needs them), and a schema-loss bug that drops the seed tenant.

### Off-host durability

A named volume on the same host protects against an accidental wipe, **not**
against losing the host. For real disaster recovery, sync BOTH `dbbackups` AND
`dbarchive` off the machine: bind-mount to a host path and back that up, or
`aws s3 sync` on a schedule. Without the archive, you can only recover to the
last logical dump (≤ 24 h RPO); with both, you recover to within minutes.
