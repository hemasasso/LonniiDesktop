#!/bin/bash
# Swaps the .NET API on OCI for the package in /tmp/lonnii-api.tgz, in one go:
#   sudo bash /tmp/deploy-api.sh
#
# Runs each step in order and stops at the first failure. Keeps a dated backup, puts back the
# server's own SQLite (the bundled one needs a newer glibc than Oracle Linux 8 has), checks
# /api/health, and if the new version does not come up, restores the backup and starts it again.
# Optional: a SQL script given as argument is applied first, as Admin_Lonnii_41 (password asked):
#   sudo bash /tmp/deploy-api.sh /tmp/031_device_hardware_signature.sql
set -euo pipefail

PKG=/tmp/lonnii-api.tgz
APP=/opt/lonnii-api
BACKUP="$APP.bak-$(date +%Y%m%d-%H%M%S)"
SQLITE=/opt/lonnii-native/libe_sqlite3.so

step() { echo; echo "==> $*"; }

[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
[ -f "$PKG" ] || { echo "Missing $PKG - copy it first: scp lonnii-api.tgz oci:/tmp/"; exit 1; }

if [ $# -ge 1 ]; then
  step "Database script $1"
  psql -h localhost -U Admin_Lonnii_41 -d lonnii_DaB_43 -v ON_ERROR_STOP=1 -f "$1"
fi

step "Stop the API"
systemctl stop lonnii-api

step "Backup to $BACKUP"
cp -a "$APP" "$BACKUP"

step "Unpack the new version"
tar -xzf "$PKG" -C "$APP"
chmod +x "$APP/Lonnii.Api"
cp -f "$SQLITE" "$APP/"
chown -R lonnii:lonnii "$APP"

step "Start the API"
systemctl start lonnii-api

step "Health check"
ok=no
for i in $(seq 1 15); do
  sleep 2
  if curl -s -m 5 http://127.0.0.1:5280/api/health | grep -q '"status":"ok"'; then ok=yes; break; fi
done

if [ "$ok" != yes ]; then
  echo "!! The new version did not answer /api/health - restoring $BACKUP"
  systemctl stop lonnii-api || true
  rm -rf "$APP"
  cp -a "$BACKUP" "$APP"
  systemctl start lonnii-api
  echo "Restored. Errors from the failed start:"
  journalctl -u lonnii-api --since "5 min ago" --no-pager | grep -iE "fail|exception|column|relation" | tail -20 || true
  exit 1
fi

echo "systemctl: $(systemctl is-active lonnii-api)"
curl -s http://127.0.0.1:5280/api/health; echo
step "Errors in the last minutes (empty is good)"
journalctl -u lonnii-api --since "3 min ago" --no-pager | grep -iE "fail|exception|column|relation" || echo "(none)"
rm -f "$PKG"
echo; echo "Done. Backup kept at $BACKUP"
