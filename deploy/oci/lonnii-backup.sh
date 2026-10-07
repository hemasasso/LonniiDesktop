#!/usr/bin/env bash
# Nightly backup of the live database and the licence server's secret key. Run by cron as root.
#
#   * database: pg_dump (custom format) as the read-only role `backup_ro`, which can read everything and write
#     nothing. The dump is checked with pg_restore --list, so a failed login can never leave an empty file that
#     looks like a backup.
#   * /var/lib/lonnii-api/jwt.key: the key every backup token is derived from. Without it, no shop's cloud
#     backup can be authorised again. Tiny.
#   * old copies beyond KEEP_DAYS are deleted.
#
# The customers' CLOUD BACKUPS (/var/lib/lonnii-api/backups) are not copied here: they are themselves backups
# and will grow. They are covered by the Oracle boot-volume backup policy instead.
#
# These copies live on the SAME disk as the database: they protect against a mistake, not against losing the
# server. Keep the Oracle boot-volume backup policy on, and pull a copy to another machine now and then.
set -euo pipefail
umask 077

DB="${LIVE_DB:-lonnii_DaB_43}"
DB_USER="${BACKUP_DB_USER:-backup_ro}"
DEST="${BACKUP_DIR:-/var/backups/lonnii}"
KEEP_DAYS="${KEEP_DAYS:-14}"
PG="${PG_BIN:-/usr/pgsql-16/bin}"
DATA_DIR="${API_DATA_DIR:-/var/lib/lonnii-api}"
STAMP="$(date +%F-%H%M)"

log() { echo "$(date '+%F %T') $*"; }
trap 'log "BACKUP FAILED (line $LINENO)"' ERR

[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }
[ -f /root/.pgpass ] || { echo "/root/.pgpass is missing (see docs/oci-deployment.md)."; exit 1; }

mkdir -p "$DEST"
chmod 700 "$DEST"

log "database dump of $DB as $DB_USER"
partial="$DEST/.db-$STAMP.dump.partial"
PGPASSFILE=/root/.pgpass "$PG/pg_dump" -h localhost -U "$DB_USER" -Fc -f "$partial" "$DB"
"$PG/pg_restore" --list "$partial" > /dev/null
mv "$partial" "$DEST/db-$STAMP.dump"

if [ -f "$DATA_DIR/jwt.key" ]; then
  log "licence server key"
  # Not `cp -p`: that keeps the original's owner and mode, which made the copy readable by everyone.
  install -m 600 -o root -g root "$DATA_DIR/jwt.key" "$DEST/jwt-$STAMP.key"
else
  log "WARNING: $DATA_DIR/jwt.key not found"
fi

log "removing copies older than $KEEP_DAYS days"
find "$DEST" -maxdepth 1 -type f \( -name 'db-*.dump' -o -name 'jwt-*.key' \) -mtime +"$KEEP_DAYS" -delete

log "done: $(find "$DEST" -maxdepth 1 -type f | wc -l) files, $(du -sh "$DEST" | cut -f1) in $DEST; latest dump $(du -h "$DEST/db-$STAMP.dump" | cut -f1)"
