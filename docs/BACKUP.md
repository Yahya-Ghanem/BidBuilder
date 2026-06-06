# Backup & restore

The bid data **is** the product, so the production stack ships a backup sidecar.
This is the operational runbook.

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
