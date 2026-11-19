#!/usr/bin/env bash
# On AWS:  sudo bash /tmp/deploy-dashboard.sh
# Replaces the dashboard site with /tmp/dashboard-build.tgz (built in WSL: dashboard/build, tarred).
# Keeps a dated copy of the current site, and puts it back if the new one does not unpack.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
PKG=/tmp/dashboard-build.tgz
HTML=/opt/bitnami/nginx/html
[ -f "$PKG" ] || { echo "Missing $PKG"; exit 1; }
tar -tzf "$PKG" index.html >/dev/null 2>&1 || tar -tzf "$PKG" ./index.html >/dev/null || { echo "$PKG has no index.html"; exit 1; }

BAK="$HTML.bak-$(date +%F_%H%M)"
cp -a "$HTML" "$BAK"
echo "Backup: $BAK"
find "$HTML" -mindepth 1 -delete
if ! tar -xzf "$PKG" -C "$HTML"; then
  echo "Unpack failed - restoring"; find "$HTML" -mindepth 1 -delete; cp -a "$BAK/." "$HTML/"; exit 1
fi
# tar from WSL can leave files 600: nginx then answers 403.
chmod -R a+rX "$HTML"
echo "Site: $(curl -sk -o /dev/null -w '%{http_code}' https://localhost/)  (expect 200)"
echo "Older copies are kept: ls -d $HTML.bak-*  (remove by hand when no longer wanted)"
