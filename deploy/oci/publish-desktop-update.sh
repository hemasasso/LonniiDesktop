#!/bin/bash
# Publishes a desktop update on OCI, in one go:
#   sudo bash /tmp/publish-desktop-update.sh
# Expects in /tmp: latest.json (signed on the PC by `lonnii-setup release`) and the installer it names.
# Online shops' computers pick it up within 6 hours (UpdateDownloader), then show the update banner.
set -euo pipefail

DIR=/var/lib/lonnii-api/updates
NOTICE=/tmp/latest.json

[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
[ -f "$NOTICE" ] || { echo "Missing $NOTICE"; exit 1; }
FILE=$(grep -o '"fileName": *"[^"]*"' "$NOTICE" | cut -d'"' -f4)
SIZE=$(grep -o '"size": *[0-9]*' "$NOTICE" | grep -o '[0-9]*$')
VERSION=$(grep -o '"version": *"[^"]*"' "$NOTICE" | cut -d'"' -f4)
[ -f "/tmp/$FILE" ] || { echo "Missing /tmp/$FILE"; exit 1; }
[ "$(stat -c %s "/tmp/$FILE")" = "$SIZE" ] || { echo "Size of /tmp/$FILE does not match latest.json - copy it again"; exit 1; }

echo "==> Publishing $VERSION ($FILE)"
mkdir -p "$DIR"
[ -f "$DIR/latest.json" ] && cp -f "$DIR/latest.json" "$DIR/latest.json.bak-$(date +%Y%m%d-%H%M%S)"
install -m 640 -o lonnii -g lonnii "/tmp/$FILE" "$DIR/$FILE"
# The notice last: a shop must never see a notice for an installer that is not there yet.
install -m 640 -o lonnii -g lonnii "$NOTICE" "$DIR/latest.json"
rm -f "/tmp/$FILE" "$NOTICE"

echo "==> In $DIR now:"
ls -la "$DIR" | grep -E "Setup|latest.json$"
echo "Done. Online shops will see $VERSION within 6 hours."
