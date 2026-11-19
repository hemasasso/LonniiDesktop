#!/usr/bin/env bash
# Restore test - on AWS, as root:  sudo bash test-oci-restore.sh [dump file]
# Proves the copy of OCI's client database really rebuilds it: loads the newest dump (or the one given) into a
# THROWAWAY database on this server, counts the rows of every table, then drops it. Touches neither OCI nor
# lonnii_dashboard. Row counts go to /root/restore-test-<date>.txt, to compare with OCI's live counts
# (command printed at the end).
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root (sudo)."; exit 1; }
PGBIN=/usr/lib/postgresql/16/bin
TEST_DB=lonnii_restore_test
DUMP="${1:-$(ls -1t /var/backups/oci-mirror/daily/db-*.dump | head -1)}"
OUT="/root/restore-test-$(date -u +%F-%H%M).txt"
psql_pg() { sudo -u postgres "$PGBIN/psql" -X -v ON_ERROR_STOP=1 "$@"; }

[ -f "$DUMP" ] || { echo "Dump not found: $DUMP"; exit 1; }
[ "$TEST_DB" != lonnii_dashboard ] || exit 1

cleanup() { psql_pg -q -d postgres -c "DROP DATABASE IF EXISTS $TEST_DB" >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo "Dump: $(basename "$DUMP") ($(du -h "$DUMP" | cut -f1))"
cleanup
psql_pg -q -d postgres -c "CREATE DATABASE $TEST_DB"

# Readable by postgres for the restore only.
tmp=$(mktemp -d); cp "$DUMP" "$tmp/d.dump"; chown -R postgres "$tmp"
start=$(date +%s)
set +e
sudo -u postgres "$PGBIN/pg_restore" --no-owner --no-acl -d "$TEST_DB" "$tmp/d.dump" 2> "$tmp/errors.txt"
set -e
rm -f "$tmp/d.dump"
echo "Restore took $(( $(date +%s) - start )) s"

errors=$(grep -c 'error:' "$tmp/errors.txt" || true)
if [ "$errors" -gt 0 ]; then
  echo "pg_restore reported $errors error(s) (first ones below):"
  grep 'error:' "$tmp/errors.txt" | head -8
else
  echo "pg_restore: no errors"
fi

COUNT_SQL="SELECT table_name || ' ' || (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM public.%I', table_name), false, true, '')))[1]::text
           FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"
psql_pg -At -d "$TEST_DB" -c "$COUNT_SQL" > "$OUT"
rm -rf "$tmp"

tables=$(wc -l < "$OUT")
rows=$(awk '{s+=$2} END {print s+0}' "$OUT")
echo
echo "Restored: $tables tables, $rows rows in total. Main tables:"
grep -E '^(groupes|users|group_members|products|ventes|vente_items|clients|caisses|stock_history|dashboard_subscriptions) ' "$OUT" | column -t || true
echo
echo "All counts saved in $OUT"
echo "Throwaway database dropped."
echo
echo "To compare with the live database, on OCI as root run:"
echo "  psql -h localhost -U backup_ro -d lonnii_DaB_43 -At -c \"$(echo "$COUNT_SQL" | tr -s ' \n' ' ')\" > /tmp/live-counts.txt"
echo "then paste both files' main-table lines (small differences = sales made since the dump)."
