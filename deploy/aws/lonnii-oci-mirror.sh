#!/usr/bin/env bash
# Nightly copy of the OCI server's backups onto this AWS server, so losing OCI loses no client data.
# Run by cron as root every 3 hours at hh:45 (OCI dumps at 02:15 nightly, and every 3 hours at hh:15).
#
#   daily/          OCI's nightly files from /var/backups/lonnii: the PostgreSQL dump (db-*.dump, all client
#                   data) and the two keys a rebuilt server needs (jwt-*.key, licence-signing-*.key).
#   shops/current/  the desktop shops' cloud backups (/var/lib/lonnii-api/backups on OCI), as of tonight.
#   shops/changed/<date>/  a shop file that changed on OCI is kept here first, so a damaged OCI
#                   cannot overwrite the good copy.
#
# Old copies are deleted after KEEP_DAYS (30), and ONLY after tonight's copy arrived and its dump passed
# pg_restore --list: on a bad night nothing is deleted. Never `--delete` against OCI: if OCI is emptied,
# this copy stays full.
#
# The result (OK or FAILED) is sent back to OCI, where the morning email reports it.
#
# Restore onto a new server: createdb lonnii_DaB_43; pg_restore -d lonnii_DaB_43 daily/db-<newest>.dump
#   jwt-<newest>.key -> /var/lib/lonnii-api/jwt.key ; licence-signing-<newest>.key -> /etc/lonnii-licence-signing.key
#   shops/current/ -> /var/lib/lonnii-api/backups/
set -euo pipefail
umask 077

OCI="${OCI_HOST:-150.136.9.41}"
KEY=/root/.ssh/oci_mirror
DEST="${MIRROR_DIR:-/var/backups/oci-mirror}"
KEEP_DAYS="${KEEP_DAYS:-30}"
PG_RESTORE="${PG_RESTORE:-/usr/lib/postgresql/16/bin/pg_restore}"
STAMP="$(date -u +%F-%H%M)"
SSH=(ssh -i "$KEY" -o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=30 -o ServerAliveInterval=30)

log() { echo "$(date -u '+%F %T') $*"; }
# Tells OCI how tonight went (the morning email reads it). Never fails the script itself.
report() { "${SSH[@]}" "lonnii_mirror@$OCI" "report-status $*" >/dev/null 2>&1 || log "could not send status to OCI"; }
fail() { log "MIRROR FAILED: $1"; report "FAILED $STAMP $1"; exit 1; }
trap 'fail "line $LINENO"' ERR

[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }
exec 9>/run/lonnii-oci-mirror.lock
flock -n 9 || { log "already running"; exit 0; }

mkdir -p "$DEST/daily" "$DEST/shops/current" "$DEST/shops/changed"
chmod 700 "$DEST"

log "OCI database dump + keys"
rsync -a --partial -e "${SSH[*]}" "lonnii_mirror@$OCI:/var/backups/lonnii/" "$DEST/daily/" || fail "copie de la base impossible"

newest="$(ls -1t "$DEST"/daily/db-*.dump 2>/dev/null | head -1 || true)"
[ -n "$newest" ] || fail "aucune sauvegarde de la base reçue"
"$PG_RESTORE" --list "$newest" > /dev/null || fail "sauvegarde $(basename "$newest") illisible"
age_h=$(( ( $(date +%s) - $(stat -c %Y "$newest") ) / 3600 ))
[ "$age_h" -le 6 ] || fail "la dernière sauvegarde d'OCI date de ${age_h} h"
size="$(du -h "$newest" | cut -f1)"
log "newest dump $(basename "$newest") ($size) is valid, ${age_h} h old"

log "shops' cloud backups"
rsync -a --partial --backup --backup-dir="../changed/$STAMP" -e "${SSH[*]}" \
  "lonnii_mirror@$OCI:/var/lib/lonnii-api/backups/" "$DEST/shops/current/" || fail "copie des sauvegardes boutiques impossible"

# Everything above succeeded: only now are old copies removed.
find "$DEST/daily" -type f -mtime +"$KEEP_DAYS" -delete
# Daytime dumps (not OCI's 02:15 one) only matter for the last few days.
find "$DEST/daily" -type f -name 'db-*.dump' ! -name 'db-*-0215.dump' -mtime +"${QUICK_KEEP_DAYS:-3}" -delete
find "$DEST/shops/changed" -mindepth 1 -maxdepth 1 -type d -mtime +"$KEEP_DAYS" -exec rm -rf {} +
find "$DEST/shops/changed" -mindepth 1 -maxdepth 1 -type d -empty -delete

dumps=$(ls -1 "$DEST"/daily/db-*.dump | wc -l)
total="$(du -sh "$DEST" | cut -f1)"
disk="$(df --output=pcent / | tail -1 | tr -dc '0-9')"
report "OK $STAMP $(basename "$newest") $size $dumps $total $disk"
log "done: $dumps dumps, $total in $DEST, AWS disk ${disk}%"
