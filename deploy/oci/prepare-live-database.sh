#!/usr/bin/env bash
# Prepares the LIVE PostgreSQL database for the Lonnii licence server (run as root on the OCI server).
#
#   1. takes a full backup of the live database (custom-format dump, mode 600),
#   2. applies db/postgres/001..015 in order, each in its own transaction, stopping at the first error,
#   3. gives the API's login (lonnii_api) read/insert/update/delete on data - and nothing that changes
#      structure - AFTER the scripts, so it also covers the tables they create,
#   4. lists the columns the .NET model expects that the database still lacks.
#
# It does NOT switch the API over: that is one line in /etc/lonnii-api.env, printed at the end, so you
# stay in control of the moment. Every script is additive (ADD COLUMN IF NOT EXISTS, CREATE TABLE IF NOT
# EXISTS); none renames, drops or retypes anything the React app uses. Run it when the app is quiet:
# ALTER TABLE takes a brief lock on each table it touches.
#
# Needs: the scripts in /tmp/pgscripts (scp -r db/postgres), /tmp/schema_check.sql, and the
# Admin_Lonnii_41 password (the DB_PASSWORD line of the React app's .env), typed when asked.
set -euo pipefail

LIVE="${LIVE_DB:-lonnii_DaB_43}"
OWNER="${DB_OWNER:-Admin_Lonnii_41}"
API_ROLE="${API_ROLE:-lonnii_api}"
PG=/usr/pgsql-16/bin
SCRIPTS="${SCRIPTS_DIR:-/tmp/pgscripts}"
BACKUP="/root/live-before-lonnii-scripts-$(date +%F-%H%M%S).dump"

[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }
[ -d "$SCRIPTS" ] || { echo "Scripts folder not found: $SCRIPTS"; exit 1; }
ls "$SCRIPTS"/0*.sql >/dev/null 2>&1 || { echo "No 0*.sql files in $SCRIPTS"; exit 1; }

read -rsp "$OWNER password: " PGPASSWORD; echo
export PGPASSWORD
PSQL=("$PG/psql" -h localhost -U "$OWNER" -d "$LIVE" -v ON_ERROR_STOP=1 -q)

echo
echo "Database: $LIVE   (this is the LIVE database)"
read -rp "Type LIVE to continue: " ok
[ "$ok" = "LIVE" ] || { echo "Cancelled. Nothing was changed."; exit 1; }

echo
echo "[1/4] Backup -> $BACKUP"
"$PG/pg_dump" -h localhost -U "$OWNER" -Fc -f "$BACKUP" "$LIVE"
chmod 600 "$BACKUP"
ls -la "$BACKUP" | cut -c1-80
echo "      Keep this file somewhere safe. It holds every customer's data: delete it when you no longer need it."

echo
echo "[2/4] Applying the scripts to $LIVE"
for f in "$SCRIPTS"/0*.sql; do
  echo "== $(basename "$f")"
  # psql's own exit status is what decides success - never a pipe's, which would hide a failure.
  rc=0
  out=$("${PSQL[@]}" -f "$f" 2>&1) || rc=$?
  echo "$out" | grep -v -E "NOTICE:.*already exists, skipping" || true
  if [ "$rc" -ne 0 ]; then
    echo "!! $(basename "$f") FAILED - stopping. The database is as it was before that script (each runs in a transaction)."
    echo "   The backup is at $BACKUP"
    exit 1
  fi
done

echo
echo "[3/4] Permissions for $API_ROLE (data only, no structure changes)"
"${PSQL[@]}" \
  -c "GRANT CONNECT ON DATABASE \"$LIVE\" TO $API_ROLE;" \
  -c "GRANT USAGE ON SCHEMA public TO $API_ROLE;" \
  -c "REVOKE ALL ON ALL TABLES IN SCHEMA public FROM $API_ROLE;" \
  -c "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $API_ROLE;" \
  -c "REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM $API_ROLE;" \
  -c "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO $API_ROLE;"

echo
echo "[4/4] Columns the .NET model expects that $LIVE still lacks"
if [ -f /tmp/schema_check.sql ]; then
  "$PG/psql" -P pager=off -h localhost -U "$OWNER" -d "$LIVE" -f /tmp/schema_check.sql
  echo "(users.first_name / last_name are expected here: the model maps onto firstname / lastname.)"
  echo "(Desktop-only tables and columns - clients, caisses, programme_*, ... - are not used by the server.)"
else
  echo "/tmp/schema_check.sql not found - skipped."
fi

unset PGPASSWORD

cat <<EOF

Done. The live database is ready. To point the API at it:

  1. nano /etc/lonnii-api.env     and change   Database=lonnii_staging   to   Database=$LIVE
  2. systemctl restart lonnii-api
  3. curl -s https://www.lonnii.com/api/health

To go back, change the name back and restart. The API only touches shops, devices, registrations and
backups; it writes nothing at start-up.
EOF
