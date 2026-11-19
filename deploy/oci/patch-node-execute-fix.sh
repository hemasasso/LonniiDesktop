#!/usr/bin/env bash
# On OCI, as root:  bash patch-node-execute-fix.sh
# Replaces ONLY the /admin/realtime/execute-fix and /execute-index handlers in the live Node backend
# (routes/realtime.js): the old ones checked the first word(s) only, so "VACUUM; <anything>",
# "CREATE INDEX ...; <anything>" or ALTER SYSTEM ran as the database superuser.
# The rest of the live file is left exactly as it is. Backup kept beside it; restored if anything fails.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }

F="$(find /var/www -path '*/node_modules' -prune -o -name realtime.js -path '*routes*' -print | head -1)"
[ -n "$F" ] || { echo "realtime.js not found under /var/www"; exit 1; }
grep -q "router.post('/execute-fix'" "$F" || { echo "No execute-fix route in $F"; exit 1; }
if grep -q "Only the exact forms the diagnostics suggest" "$F"; then echo "Already patched: $F"; exit 0; fi

BAK="$F.bak-$(date +%F-%H%M)"
cp -a "$F" "$BAK"
echo "File:   $F"
echo "Backup: $BAK"
restore() { echo "FAILED - restoring the backup"; cp -a "$BAK" "$F"; exit 1; }

FIX="$(mktemp)"; IDX="$(mktemp)"
cat > "$FIX" <<'HANDLER'
router.post('/execute-fix', async (req, res) => {
  try {
    const { sql, type } = req.body;
    if (typeof sql !== 'string' || !sql.trim()) return res.status(400).json({ error: 'SQL requis' });

    // Only the exact forms the diagnostics suggest, rebuilt here rather than run as sent. The old check
    // looked at the first word only, so "VACUUM; <anything>" or ALTER SYSTEM (shell commands through
    // archive_command) ran as the database superuser.
    const text = sql.replace(/--[^\n]*$/gm, '').trim().replace(/;\s*$/, '').trim();
    const ident = '([A-Za-z_][A-Za-z0-9_]{0,62})';
    let m;
    let statement;
    let params = [];
    if ((m = text.match(new RegExp(`^VACUUM(?:\\s+ANALYZE)?(?:\\s+${ident})?$`, 'i')))) {
      statement = `VACUUM ANALYZE${m[1] ? ` "${m[1]}"` : ''}`;
    } else if ((m = text.match(new RegExp(`^ANALYZE(?:\\s+${ident})?$`, 'i')))) {
      statement = `ANALYZE${m[1] ? ` "${m[1]}"` : ''}`;
    } else if ((m = text.match(new RegExp(`^REINDEX\\s+TABLE\\s+${ident}$`, 'i')))) {
      statement = `REINDEX TABLE "${m[1]}"`;
    } else if ((m = text.match(/^SELECT\s+PG_TERMINATE_BACKEND\s*\(\s*(\d{1,9})\s*\)$/i))) {
      statement = 'SELECT pg_terminate_backend($1)';
      params = [Number(m[1])];
    } else {
      return res.status(400).json({ error: 'Commande non autorisée. Seuls VACUUM, ANALYZE, REINDEX TABLE et pg_terminate_backend(pid) sont permis, une seule commande à la fois.' });
    }

    console.log(`[REALTIME] Executing fix (${type || 'unknown'}) by admin ${req.dashboardAdmin?.id ?? '?'}:`, statement, params);
    await pool.query(statement, params);

    res.json({ success: true, message: 'Correction appliquée avec succès' });
  } catch (error) {
    console.error('[REALTIME] Error executing fix:', error);
    res.status(500).json({ error: 'Erreur lors de l\'exécution', details: error.message });
  }
});
HANDLER
cat > "$IDX" <<'HANDLER'
router.post('/execute-index', async (req, res) => {
  try {
    const { sql } = req.body;
    if (typeof sql !== 'string' || !sql.trim()) return res.status(400).json({ error: 'SQL requis' });

    // One index command of the exact shape the diagnostics suggest, rebuilt here: the old check looked at
    // the first words only, so "CREATE INDEX ...; <anything>" ran as sent.
    const text = sql.replace(/--[^\n]*$/gm, '').trim().replace(/;\s*$/, '').trim();
    const id = '([A-Za-z_][A-Za-z0-9_]{0,62})';
    let m;
    let statement;
    if ((m = text.match(new RegExp(`^CREATE\\s+INDEX\\s+${id}\\s+ON\\s+${id}\\s*\\(\\s*${id}\\s*\\)$`, 'i')))) {
      statement = `CREATE INDEX IF NOT EXISTS "${m[1]}" ON "${m[2]}" ("${m[3]}")`;
    } else if ((m = text.match(new RegExp(`^DROP\\s+INDEX\\s+(?:IF\\s+EXISTS\\s+)?${id}$`, 'i')))) {
      statement = `DROP INDEX IF EXISTS "${m[1]}"`;
    } else {
      return res.status(400).json({ error: 'Seules les commandes CREATE INDEX nom ON table(colonne) et DROP INDEX nom sont autorisées, une seule à la fois.' });
    }

    console.log('[REALTIME] Executing index command:', statement);
    await pool.query(statement);

    res.json({ success: true, message: 'Commande exécutée avec succès' });
  } catch (error) {
    console.error('[REALTIME] Error executing index command:', error);
    res.status(500).json({ error: 'Erreur lors de l\'exécution', details: error.message });
  }
});
HANDLER

# Swap a handler: from its first line to the first line that is exactly "});" after it.
cat > /tmp/swap-handler.cjs <<'JS'
const fs = require('fs');
const [file, route, repl] = process.argv.slice(2);
const lines = fs.readFileSync(file, 'utf8').split('\n');
const start = lines.findIndex(l => l.startsWith(`router.post('${route}'`));
let end = -1;
for (let i = start + 1; start >= 0 && i < lines.length; i++) if (lines[i] === '});') { end = i; break; }
if (start < 0 || end < 0) { console.error(`${route}: handler bounds not found`); process.exit(1); }
const out = [...lines.slice(0, start), ...fs.readFileSync(repl, 'utf8').replace(/\n$/, '').split('\n'), ...lines.slice(end + 1)];
fs.writeFileSync(file, out.join('\n'));
console.log(`${route}: replaced lines ${start + 1}-${end + 1}`);
JS
node /tmp/swap-handler.cjs "$F" /execute-fix "$FIX" || restore
if grep -q "router.post('/execute-index'" "$F"; then node /tmp/swap-handler.cjs "$F" /execute-index "$IDX" || restore; fi
rm -f "$FIX" "$IDX" /tmp/swap-handler.cjs

# ES module syntax check on a copy (the backend is "type": "module").
cp "$F" /tmp/realtime-check.mjs
node --check /tmp/realtime-check.mjs || { rm -f /tmp/realtime-check.mjs; restore; }
rm -f /tmp/realtime-check.mjs
chown --reference="$BAK" "$F"; chmod --reference="$BAK" "$F"

pm2 reload lonnii-backend --update-env >/dev/null
sleep 3
echo "Workers online: $(pm2 jlist | grep -o '"status":"online"' | wc -l)"
echo "Check: curl -s -o /dev/null -w '%{http_code}\n' -X POST https://www.lonnii.com/admin/realtime/execute-fix  (expect 401)"
