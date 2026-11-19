#!/usr/bin/env bash
# On OCI, as root:   bash node-limited-db-role.sh          switch the Node backend to a limited login
#                    bash node-limited-db-role.sh revert   put the previous (superuser) login back
#
# The Node backend logged in to PostgreSQL as a superuser, so any hole in it (like execute-fix) was full
# control of the database and of the server. This creates `lonnii_node`, which can only:
#   * read, add, change and delete ROWS in the existing tables (what the app does), in every app schema
#   * use the sequences, and create new tables in public (push_tokens is created "if not exists")
#   * read the server statistics (pg_monitor, Temps réel page) and stop a stuck connection (pg_signal_backend)
# It cannot drop or alter tables, change server settings, read files or run programs. Tables created later by
# the usual owner get the same rights automatically (default privileges).
# Not given (owner-only in PostgreSQL 16): VACUUM / REINDEX / CREATE INDEX from the dashboard - do those by hand.
#
# No data is touched. The previous settings file is kept as /var/www/.env.bak-<date>.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }
ENV=/var/www/.env
PSQL=/usr/pgsql-16/bin/psql
ROLE=lonnii_node

env_get() { grep -E "^$1=" "$ENV" | tail -1 | cut -d= -f2- | sed 's/^"//; s/"$//; s/^'"'"'//; s/'"'"'$//'; }
reload() {
  pm2 reload lonnii-backend --update-env >/dev/null; sleep 5
  echo "Workers online: $(pm2 jlist | grep -o '"status":"online"' | wc -l)"
  echo "Website: $(curl -s -o /dev/null -w '%{http_code}' https://www.lonnii.com/)"
}

if [ "${1:-}" = revert ]; then
  last="$(ls -1t $ENV.bak-* 2>/dev/null | head -1 || true)"
  [ -n "$last" ] || { echo "No $ENV.bak-* to restore."; exit 1; }
  cp -a "$last" "$ENV"; echo "Restored $last"; reload; exit 0
fi

DB_USER="$(env_get DB_USER)"; DB_PASSWORD="$(env_get DB_PASSWORD)"
DB_HOST="$(env_get DB_HOST)"; DB_PORT="$(env_get DB_PORT)"; DB_NAME="$(env_get DB_NAME)"
DB_HOST=${DB_HOST:-localhost}; DB_PORT=${DB_PORT:-5432}
[ "$DB_USER" != "$ROLE" ] || { echo "Already using $ROLE."; exit 0; }
[ -n "$DB_USER" ] && [ -n "$DB_NAME" ] || { echo "DB_USER/DB_NAME not found in $ENV"; exit 1; }
echo "Current login: $DB_USER on $DB_NAME ($DB_HOST:$DB_PORT)"

NEWPASS="$(openssl rand -hex 24)"
export PGPASSWORD="$DB_PASSWORD"
"$PSQL" -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 -q \
  -v role="$ROLE" -v pass="$NEWPASS" -v db="$DB_NAME" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN', :'role') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'role') \gexec
SELECT format('ALTER ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L', :'role', :'pass') \gexec
SELECT format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO %I', :'db', :'role') \gexec
SELECT format('GRANT pg_monitor, pg_signal_backend TO %I', :'role') \gexec
SELECT format('GRANT CREATE ON SCHEMA public TO %I', :'role') \gexec
-- Every application schema (public and any per-shop ones): rows and sequences only.
SELECT format('GRANT USAGE ON SCHEMA %I TO %I', nspname, :'role')
  FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' \gexec
SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I', nspname, :'role')
  FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' \gexec
SELECT format('GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %I TO %I', nspname, :'role')
  FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' \gexec
SELECT format('GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA %I TO %I', nspname, :'role')
  FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' \gexec
-- Tables the usual owners create later get the same rights.
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', o, :'role')
  FROM (SELECT DISTINCT tableowner AS o FROM pg_tables WHERE schemaname NOT LIKE 'pg\_%' AND schemaname <> 'information_schema') t
  WHERE o <> :'role' \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I', o, :'role')
  FROM (SELECT DISTINCT tableowner AS o FROM pg_tables WHERE schemaname NOT LIKE 'pg\_%' AND schemaname <> 'information_schema') t
  WHERE o <> :'role' \gexec
SQL
echo "Role $ROLE ready."

# Prove the new login works and sees every table before switching anything.
export PGPASSWORD="$NEWPASS"
check="$("$PSQL" -h "$DB_HOST" -p "$DB_PORT" -U "$ROLE" -d "$DB_NAME" -At -c \
  "SELECT count(*) || ' tables, ' || count(*) FILTER (WHERE NOT has_table_privilege(format('%I.%I', schemaname, tablename), 'SELECT,INSERT,UPDATE,DELETE')) || ' without access, superuser=' || (SELECT rolsuper FROM pg_roles WHERE rolname = current_user)
     FROM pg_tables WHERE schemaname NOT LIKE 'pg\_%' AND schemaname <> 'information_schema'")"
echo "New login check: $check"
case "$check" in *" 0 without access, superuser=f"|*" 0 without access, superuser=false") ;; *) echo "Check failed - nothing switched."; exit 1 ;; esac
"$PSQL" -h "$DB_HOST" -p "$DB_PORT" -U "$ROLE" -d "$DB_NAME" -At -c "SELECT 'groupes: ' || count(*) FROM groupes"
unset PGPASSWORD

# Switch the backend: settings file backed up first.
BAK="$ENV.bak-$(date +%F-%H%M)"
cp -a "$ENV" "$BAK"; echo "Settings backup: $BAK"
# The file may lack a final newline (seen before): add one before editing.
[ -z "$(tail -c1 "$ENV")" ] || printf '\n' >> "$ENV"
sed -i -E "s/^DB_USER=.*/DB_USER=$ROLE/; s/^DB_PASSWORD=.*/DB_PASSWORD=$NEWPASS/" "$ENV"
grep -q "^DB_USER=$ROLE$" "$ENV" || { cp -a "$BAK" "$ENV"; echo "Edit failed - restored."; exit 1; }

reload
echo
echo "Permission errors in the backend log since the switch (should be none; a few at start-up are expected"
echo "for 'ALTER TABLE users ADD COLUMN IF NOT EXISTS last_login', which is caught and harmless):"
sleep 20
pm2 logs lonnii-backend --nostream --lines 300 2>/dev/null | grep -i "permission denied\|must be owner" | tail -10 || true
echo
echo "Use the app and the dashboard for a day. If anything says 'permission denied': bash $0 revert"
