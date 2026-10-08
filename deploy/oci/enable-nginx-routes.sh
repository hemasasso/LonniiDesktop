#!/usr/bin/env bash
# Routes the licence-server paths of www.lonnii.com to the .NET API on 127.0.0.1:5280 (run as root).
#
# Adds ONE include line to the site's nginx file and writes two small files beside nginx's own.
# Backs the site file up first, runs `nginx -t`, and puts the original back if the test fails -
# the React site keeps serving either way. Safe to run again (the include is only added once).
set -euo pipefail

SITE=/www/server/panel/vhost/nginx/www.lonnii.com.conf
CONF=/www/server/nginx/conf
BACKUP=/root/www.lonnii.com.conf.bak-$(date +%F-%H%M%S)

[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }
[ -f "$SITE" ] || { echo "Site file not found: $SITE"; exit 1; }
[ -d "$CONF" ] || { echo "nginx conf folder not found: $CONF"; exit 1; }

cp "$SITE" "$BACKUP"
echo "backup: $BACKUP"

cat > "$CONF/lonnii-api-proxy.conf" <<'EOF'
proxy_http_version 1.1;
proxy_set_header Host $host;
# www.lonnii.com is behind Cloudflare, so $remote_addr is a Cloudflare address shared by everyone.
# The visitor's real address is in CF-Connecting-IP. Always overwritten, never appended: the API
# trusts this header, and the per-address sign-up limit depends on it.
proxy_set_header X-Forwarded-For $http_cf_connecting_ip;
proxy_set_header X-Forwarded-Proto $scheme;
EOF

# limit_req_zone only works at http level; aaPanel includes every file in its vhost folder there.
RATELIMIT=/www/server/panel/vhost/nginx/lonnii-ratelimit.conf
cat > "$RATELIMIT" <<'EOF'
# Sign-in attempts: 10 a minute per visitor address (burst 5 on top).
limit_req_zone $http_cf_connecting_ip zone=lonnii_login:10m rate=10r/m;
EOF

cat > "$CONF/lonnii-api.conf" <<'EOF'
# Lonnii .NET API on 127.0.0.1:5280. Only the paths the desktop app and the mobile app need;
# everything else under /api/ still goes to Node. Checked: Node has no route under /api/auth,
# /api/groupes, /api/groupe, /api/privileges or /api/live, so these never shadow it.
location = /api/health          { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/activation      { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/activation/    { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/registration/  { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/licence/       { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }

# Mobile app and remote admins: sign-in, shops, privileges, and the live-change counter.
location = /api/auth/login {
    limit_req zone=lonnii_login burst=5 nodelay;    # slow password guessing; zone in lonnii-ratelimit.conf
    limit_req_status 429;
    proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf;
}
location ^~ /api/auth/          { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/groupes         { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/groupes/       { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/groupe/        { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; client_max_body_size 10m; }
location ^~ /api/privileges/    { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/live/          { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/remote/        { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
# Gestion modules for the mobile app. Node serves its own at the root (/ventes, /gestion), so
# nothing under these /api/ paths collides. /api/parametres is routed only for /recu (below):
# Node owns /api/parametres/ventes.
location = /api/ventes        { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/ventes/       { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/caisse        { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/caisse/       { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/stock         { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/stock/        { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/charges       { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/charges/      { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/marges        { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/marges/       { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/bilan         { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/bilan/        { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/amortissement { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/amortissement/{ proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/audit         { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/audit/        { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/programme     { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/programme/    { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
# Product and category pictures served by the .NET API (the old Node uploads stay at /uploads).
location = /api/images         { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/images/       { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
# Receipt settings: only /api/parametres/recu goes to .NET. Node keeps /api/parametres/ventes/...
location = /api/parametres/recu   { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/parametres/recu/ { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; client_max_body_size 10m; }
location = /api/parametres/consommation { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/backup/ {
    proxy_pass http://127.0.0.1:5280;
    include /www/server/nginx/conf/lonnii-api-proxy.conf;
    client_max_body_size 2g;        # cloud backup snapshots
    proxy_request_buffering off;    # stream uploads instead of writing them to disk first
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
}
EOF

if ! grep -q "lonnii-api.conf" "$SITE"; then
  grep -q "    # API routes" "$SITE" || { echo "Could not find the '# API routes' line in $SITE - nothing changed."; exit 1; }
  sed -i '0,/    # API routes/s||    include /www/server/nginx/conf/lonnii-api.conf;\n    # API routes|' "$SITE"
fi

if nginx -t; then
  systemctl reload nginx
  echo "nginx reloaded."
else
  cp "$BACKUP" "$SITE"
  rm -f "$RATELIMIT"
  echo "nginx test FAILED - the original site file was restored. Nothing changed."
  nginx -t || true
  exit 1
fi

echo
echo -n "licence server health : "; curl -s https://www.lonnii.com/api/health || echo "(no answer)"
echo
echo -n "React site            : "; curl -s -o /dev/null -w "%{http_code}\n" https://www.lonnii.com/
