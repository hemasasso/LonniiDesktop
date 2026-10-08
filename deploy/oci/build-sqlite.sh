#!/usr/bin/env bash
# Builds SQLite on the OCI server itself and puts it where the Lonnii API loads it (run as root).
#
# Why: the libe_sqlite3.so that .NET bundles for linux-arm64 needs glibc 2.33/2.34, and Oracle Linux 8
# has 2.28, so the API cannot open any SQLite file there ("Unable to load shared library 'e_sqlite3'").
# The licence server only needs SQLite to read a shop's backup copy (remote access), but without it that
# fails with a 500. Compiling the official source here makes a library that matches this system.
#
# The result is kept in /opt/lonnii-native and copied into /opt/lonnii-api. Every API redeploy overwrites
# /opt/lonnii-api/libe_sqlite3.so with the bundled one again, so after each deploy run:
#     cp /opt/lonnii-native/libe_sqlite3.so /opt/lonnii-api/ && systemctl restart lonnii-api
# Safe to run again.
set -euo pipefail

VERSION=3530400                                   # SQLite 3.53.4
SHA3=628a44cfe82c66aed1ccbbe85a562d2e33ebe64b3288981ed76285612227934e
ZIP=sqlite-amalgamation-$VERSION.zip

[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }

dnf install -y gcc unzip >/dev/null

WORK=$(mktemp -d)
cd "$WORK"

for year in 2026 2025; do
  if curl -fsSLO "https://www.sqlite.org/$year/$ZIP"; then break; fi
done
[ -f "$ZIP" ] || { echo "Download failed: $ZIP not found on sqlite.org."; exit 1; }

# The fingerprint sqlite.org publishes for this file: refuse anything else.
GOT=$(openssl dgst -sha3-256 "$ZIP" | awk '{print $NF}')
[ "$GOT" = "$SHA3" ] || { echo "Checksum mismatch ($GOT). Not installing."; exit 1; }

unzip -q "$ZIP"
cd "sqlite-amalgamation-$VERSION"

# Options close to the e_sqlite3 build the .NET provider expects (column metadata is required by it).
gcc -O2 -fPIC -shared -o libe_sqlite3.so sqlite3.c \
  -DSQLITE_ENABLE_COLUMN_METADATA \
  -DSQLITE_ENABLE_FTS4 -DSQLITE_ENABLE_FTS5 \
  -DSQLITE_ENABLE_RTREE \
  -DSQLITE_ENABLE_MATH_FUNCTIONS \
  -DSQLITE_ENABLE_SNAPSHOT \
  -DSQLITE_DEFAULT_FOREIGN_KEYS=1 \
  -lpthread -ldl -lm

install -d /opt/lonnii-native
install -m 755 libe_sqlite3.so /opt/lonnii-native/libe_sqlite3.so

if [ -d /opt/lonnii-api ]; then
  cp /opt/lonnii-native/libe_sqlite3.so /opt/lonnii-api/libe_sqlite3.so
  chown lonnii:lonnii /opt/lonnii-api/libe_sqlite3.so 2>/dev/null || true
  systemctl restart lonnii-api
  sleep 5
  echo "service: $(systemctl is-active lonnii-api)"
fi

echo "glibc needed by the new library:"
objdump -T /opt/lonnii-native/libe_sqlite3.so 2>/dev/null | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1 || true
echo "system glibc: $(ldd --version | head -1)"

rm -rf "$WORK"
echo "SQLite $VERSION installed in /opt/lonnii-native and /opt/lonnii-api."
