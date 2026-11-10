#!/bin/bash
# Morning health report of the OCI server, by email. Run by cron as root
# (/etc/cron.d/lonnii-daily-report, 06:00 New York time). Reads only: it changes nothing on the server.
#
#   Services   .NET API (+ /api/health), nginx, PostgreSQL, Node backend (pm2 workers online)
#   Backup     last night's pg_dump in /var/backups/lonnii (age + size)
#   Disk       / and /boot (warns above 80 %)
#   Errors     exceptions the .NET API logged in the last 24 h, most frequent first
#   HTTPS      days left on www.lonnii.com's certificate
#   Shops      registrations awaiting approval, online shops whose cloud backup is older than 24 h,
#              subscriptions ending within 7 days
#
# Email: the API's own SMTP settings (/etc/lonnii-api.env, Lonnii__Email__*), sent with curl.
# Database: read-only role backup_ro (password in /root/.pgpass), the same one the nightly backup uses.
set -u

REPORT_TO="hemasasso@outlook.com"
DB="lonnii_DaB_43"
DATA_DIR=/var/lib/lonnii-api
BACKUP_DIR=/var/backups/lonnii
SITE=www.lonnii.com

# Every result is "section|ok-or-warn|text"; the email groups them into one card per section.
SEC=""
RESULTS=()
WARN=()
ok()   { RESULTS+=("$SEC|ok|$*"); }
warn() { RESULTS+=("$SEC|warn|$*"); WARN+=("$*"); }
DISKS=()   # "mount|percent|used|size" for the gauges
sql()  { psql -h localhost -U backup_ro -d "$DB" -At -F '|' -c "$1" 2>/dev/null; }

SEC="Services"
# --- Services -----------------------------------------------------------------------------------
for unit in lonnii-api nginx postgresql-16 pm2-root; do
  if systemctl is-active --quiet "$unit"; then ok "Service $unit : actif"; else warn "Service $unit : ARRÊTÉ"; fi
done

if health=$(curl -s -m 10 http://127.0.0.1:5280/api/health) && echo "$health" | grep -q '"status":"ok"'; then
  ok "API .NET : répond (santé ok)"
else
  warn "API .NET : ne répond pas à /api/health"
fi

workers=$(pm2 jlist 2>/dev/null | grep -o '"status":"online"' | wc -l)
if [ "${workers:-0}" -ge 1 ]; then ok "Backend Node : $workers processus en ligne"; else warn "Backend Node : aucun processus pm2 en ligne"; fi

SEC="Sauvegardes"
# --- Nightly database backup --------------------------------------------------------------------
last=$(ls -1t "$BACKUP_DIR"/db-*.dump 2>/dev/null | head -1)
if [ -z "$last" ]; then
  warn "Sauvegarde de la base : aucune trouvée dans $BACKUP_DIR"
else
  age_h=$(( ( $(date +%s) - $(stat -c %Y "$last") ) / 3600 ))
  size=$(du -h "$last" | cut -f1)
  if [ "$age_h" -le 26 ]; then ok "Sauvegarde de la base : il y a ${age_h} h ($size)"; else warn "Sauvegarde de la base : la dernière date de ${age_h} h ($size)"; fi
fi

SEC="Disque"
# --- Disk -----------------------------------------------------------------------------------------
for mount in / /boot; do
  used=$(df --output=pcent "$mount" 2>/dev/null | tail -1 | tr -dc '0-9')
  [ -z "$used" ] && continue
  DISKS+=("$mount|$used|$(df -h --output=used "$mount" | tail -1 | tr -d ' ')|$(df -h --output=size "$mount" | tail -1 | tr -d ' ')")
  if [ "$used" -lt 80 ]; then ok "Disque $mount : ${used} % utilisé"; else warn "Disque $mount : ${used} % utilisé"; fi
done

SEC="Erreurs"
# --- API errors in the last 24 h ------------------------------------------------------------------
errors=$(journalctl -u lonnii-api --since "24 hours ago" --no-pager 2>/dev/null | grep -i "exception")
count=$(printf '%s\n' "$errors" | grep -c . )
if [ "$count" -eq 0 ]; then
  ok "Erreurs API (24 h) : aucune"
else
  top=$(printf '%s\n' "$errors" | grep -oE '[A-Za-z.]*Exception[^:]*' | sort | uniq -c | sort -rn | head -1 | sed 's/^ *//')
  warn "Erreurs API (24 h) : $count ligne(s) - la plus fréquente : $top"
fi

SEC="Sécurité"
# --- HTTPS certificate ----------------------------------------------------------------------------
end=$(echo | timeout 10 openssl s_client -connect "$SITE:443" -servername "$SITE" 2>/dev/null | openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2)
if [ -n "$end" ]; then
  days=$(( ( $(date -d "$end" +%s) - $(date +%s) ) / 86400 ))
  if [ "$days" -ge 14 ]; then ok "Certificat HTTPS : $days jours restants"; else warn "Certificat HTTPS : expire dans $days jours"; fi
else
  warn "Certificat HTTPS : impossible de le lire"
fi

SEC="Boutiques"
# --- Shops ----------------------------------------------------------------------------------------
pending=$(sql "SELECT count(*) FROM groupes WHERE approval_status = 'pending'")
if [ -z "$pending" ]; then warn "Base : lecture impossible (backup_ro)"
elif [ "$pending" -eq 0 ]; then ok "Inscriptions en attente : aucune"
else warn "Inscriptions en attente : $pending à approuver"; fi

SEC="Sauvegardes"
stale=()
while IFS='|' read -r gid nom; do
  [ -z "$gid" ] && continue
  newest=$(ls -1t "$DATA_DIR/backups/$gid/snapshots"/snapshot-*.db.gz 2>/dev/null | head -1)
  if [ -z "$newest" ]; then stale+=("$nom (aucune)"); continue; fi
  h=$(( ( $(date +%s) - $(stat -c %Y "$newest") ) / 3600 ))
  [ "$h" -gt 24 ] && stale+=("$nom (${h} h)")
done < <(sql "SELECT id, nom FROM groupes WHERE mode = 'online' AND approval_status = 'approved' AND COALESCE(phone_only, false) = false")
if [ ${#stale[@]} -eq 0 ]; then ok "Sauvegardes cloud des boutiques en ligne : à jour"
else warn "Sauvegarde cloud de plus de 24 h : $(printf '%s, ' "${stale[@]}" | sed 's/, $//')"; fi

SEC="Boutiques"
ending=$(sql "SELECT string_agg(group_name || ' (' || to_char(contract_end_date, 'DD/MM') || ')', ', ')
              FROM dashboard_subscriptions
              WHERE statut = 'Active' AND contract_end_date BETWEEN now() AND now() + interval '7 days'")
if [ -z "$ending" ]; then ok "Abonnements finissant sous 7 jours : aucun"; else warn "Abonnements finissant sous 7 jours : $ending"; fi

# --- Compose ----------------------------------------------------------------------------------------
today=$(TZ=America/New_York date '+%d/%m/%Y')
if [ ${#WARN[@]} -eq 0 ]; then
  verdict="Tout est OK"
  summary="Serveur en marche, sauvegarde faite, aucune alerte."
else
  verdict="${#WARN[@]} point(s) à vérifier"
  summary="${WARN[0]}"
fi
subject="Lonnii - rapport du $today : $verdict"

# Dark cards like the dashboard: a round check (green) or ! (orange) per section, ring gauges for the disks.
F="font-family:Segoe UI,Helvetica,Arial,sans-serif"
badge() { # $1 = ok|warn, $2 = size px
  local bg="#10b981" mark="&#10003;"; [ "$1" = warn ] && { bg="#f59e0b"; mark="!"; }
  echo "<span style='display:inline-block;width:$2px;height:$2px;line-height:$2px;border-radius:50%;background:$bg;color:#fff;text-align:center;font-weight:700;font-size:$(( $2 / 2 + 2 ))px;$F'>$mark</span>"
}
card_open()  { echo "<table role='presentation' width='100%' cellpadding='0' cellspacing='0' style='background:#111827;border:1px solid #1f2937;border-radius:12px;margin:0 0 14px 0'><tr><td style='padding:16px 18px;$F'>"; }
card_close() { echo "</td></tr></table>"; }

if [ ${#WARN[@]} -eq 0 ]; then overall=ok; vcol="#10b981"; else overall=warn; vcol="#f59e0b"; fi
html="<div style='background:#0b0f17;padding:22px;$F'>"
html+="<table role='presentation' width='100%' cellpadding='0' cellspacing='0' style='max-width:640px;margin:0 auto'><tr><td>"
html+="<table role='presentation' cellpadding='0' cellspacing='0' style='margin-bottom:16px'><tr><td style='padding-right:14px'>$(badge $overall 46)</td>"
html+="<td style='$F'><div style='color:#f9fafb;font-size:20px;font-weight:700'>Lonnii - rapport du serveur</div>"
html+="<div style='color:#9ca3af;font-size:14px'>$today &middot; <b style='color:$vcol'>$verdict</b></div></td></tr></table>"

# Système
os=$(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-Linux}")
up=$(uptime -p 2>/dev/null | sed 's/^up //; s/ weeks\?/sem/; s/ days\?/j/; s/ hours\?/h/; s/ minutes\?/m/; s/,//g')
html+="$(card_open)<div style='color:#e5e7eb;font-size:15px;font-weight:700;margin-bottom:10px'>Système</div>"
for kv in "OS|$os" "Uptime|$up" "Host|$(hostname)"; do
  html+="<div style='background:#0b0f17;border:1px solid #1f2937;border-radius:8px;padding:9px 12px;margin-bottom:6px'><span style='color:#9ca3af'>${kv%%|*}</span><span style='float:right;color:#f9fafb;font-weight:600'>${kv#*|}</span></div>"
done
html+="<div style='background:#0b0f17;border:1px solid #1f2937;border-radius:8px;padding:9px 12px'><span style='color:#9ca3af'>Statut</span><span style='float:right;color:#10b981;font-weight:700'>EN LIGNE</span></div>$(card_close)"

# Disque: ring gauges
if [ ${#DISKS[@]} -gt 0 ]; then
  html+="$(card_open)<div style='color:#e5e7eb;font-size:15px;font-weight:700;margin-bottom:12px'>Disque</div><table role='presentation' cellpadding='0' cellspacing='0'><tr>"
  for d in "${DISKS[@]}"; do
    IFS='|' read -r m pct usedh sizeh <<< "$d"
    col="#10b981"; [ "$pct" -ge 80 ] && col="#f59e0b"
    html+="<td style='padding-right:26px;text-align:center;$F'><div style='width:96px;height:96px;border-radius:50%;border:9px solid $col;box-sizing:border-box;line-height:78px;color:#f9fafb;font-size:20px;font-weight:700;text-align:center'>${pct}%</div>"
    html+="<div style='color:#9ca3af;font-size:12px;margin-top:6px'>$m &middot; $usedh / $sizeh</div></td>"
  done
  html+="</tr></table>$(card_close)"
fi

# One card per section, check or ! in the title and on each line
for sec in Services Sauvegardes Erreurs "Sécurité" Boutiques; do
  state=ok; lines=""
  for r in "${RESULTS[@]}"; do
    [ "${r%%|*}" = "$sec" ] || continue
    rest=${r#*|}; st=${rest%%|*}; txt=${rest#*|}
    tcol="#d1d5db"; [ "$st" = warn ] && { state=warn; tcol="#fbbf24"; }
    lines+="<tr><td style='padding:5px 10px 5px 0;vertical-align:top'>$(badge $st 18)</td><td style='padding:5px 0;color:$tcol;font-size:14px;$F'>$txt</td></tr>"
  done
  [ -z "$lines" ] && continue
  html+="$(card_open)<table role='presentation' cellpadding='0' cellspacing='0' style='margin-bottom:8px'><tr><td style='padding-right:10px'>$(badge $state 26)</td><td style='color:#e5e7eb;font-size:15px;font-weight:700;$F'>$sec</td></tr></table>"
  html+="<table role='presentation' cellpadding='0' cellspacing='0'>$lines</table>$(card_close)"
done

html+="<div style='color:#6b7280;font-size:11px;text-align:center;margin-top:6px'>Envoyé par $(hostname) &middot; /usr/local/sbin/lonnii-daily-report.sh</div>"
html+="</td></tr></table></div>"

# --- Email (the API's SMTP settings) ------------------------------------------------------------
env_get() { grep -E "^$1=" /etc/lonnii-api.env 2>/dev/null | tail -1 | cut -d= -f2- | sed 's/^"//; s/"$//'; }
host=$(env_get Lonnii__Email__Host); port=$(env_get Lonnii__Email__Port); port=${port:-587}
user=$(env_get Lonnii__Email__User); pass=$(env_get Lonnii__Email__Password); from=$(env_get Lonnii__Email__From)
if [ -n "$host" ] && [ -n "$from" ]; then
  scheme=smtp; [ "$port" = "465" ] && scheme=smtps
  mail=$(mktemp)
  {
    echo "From: Lonnii <$from>"
    echo "To: $REPORT_TO"
    # Outlook distrusts a message without these two, more so a styled one.
    echo "Date: $(LC_ALL=C date -R)"
    echo "Message-ID: <$(date +%s).$$.report@lonnii.com>"
    echo "Subject: =?UTF-8?B?$(printf '%s' "$subject" | base64 -w0)?="
    echo "MIME-Version: 1.0"
    echo "Content-Type: text/html; charset=UTF-8"
    echo
    # One element per line: mail servers fold lines over ~1000 characters, which split a word in two.
    echo "$html" | sed 's/<tr>/\n<tr>/g; s/<table/\n<table/g; s/<div/\n<div/g'
  } > "$mail"
  auth=(); [ -n "$user" ] && auth=(--user "$user:$pass")
  if ! curl -s -m 30 --ssl-reqd --url "$scheme://$host:$port" "${auth[@]}" \
        --mail-from "$from" --mail-rcpt "$REPORT_TO" -T "$mail"; then
    echo "$(date '+%F %T') email failed" >&2
  fi
  rm -f "$mail"
else
  echo "$(date '+%F %T') no SMTP settings in /etc/lonnii-api.env" >&2
fi

echo "$(date '+%F %T') report sent: $verdict"
