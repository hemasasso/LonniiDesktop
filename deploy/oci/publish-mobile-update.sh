#!/bin/bash
# Publishes a phone APK on OCI, in one go:
#   sudo bash /tmp/publish-mobile-update.sh
# Expects in /tmp: mobile-latest.json ({"versionCode":..,"versionName":"..","fileName":"..","notes":".."})
# and the APK it names. Phones see the update banner (AppUpdateBanner) at their next check.
set -euo pipefail

DIR=/var/lib/lonnii-api/updates/mobile
NOTICE=/tmp/mobile-latest.json

[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
[ -f "$NOTICE" ] || { echo "Missing $NOTICE"; exit 1; }
FILE=$(grep -o '"fileName": *"[^"]*"' "$NOTICE" | cut -d'"' -f4)
VERSION=$(grep -o '"versionName": *"[^"]*"' "$NOTICE" | cut -d'"' -f4)
[ -f "/tmp/$FILE" ] || { echo "Missing /tmp/$FILE"; exit 1; }

echo "==> Publishing phone $VERSION ($FILE)"
mkdir -p "$DIR"
# The folder needs x (traverse) for the API's account: it was once created drw-r----- and unreadable.
chown lonnii:lonnii "$DIR"; chmod 750 "$DIR"
[ -f "$DIR/latest.json" ] && cp -f "$DIR/latest.json" "$DIR/latest.json.bak-$(date +%Y%m%d-%H%M%S)"
install -m 640 -o lonnii -g lonnii "/tmp/$FILE" "$DIR/$FILE"
# The notice last: a phone must never see a notice for an APK that is not there yet.
install -m 640 -o lonnii -g lonnii "$NOTICE" "$DIR/latest.json"
rm -f "/tmp/$FILE" "$NOTICE"

echo "==> In $DIR now:"
ls -la "$DIR" | grep -E "\.apk|latest.json$"
echo "Check: curl -s http://127.0.0.1:5280/api/mobile/update/latest"
echo "Done."
