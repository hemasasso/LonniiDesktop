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

cat > "$CONF/lonnii-api.conf" <<'EOF'
# Lonnii licence server (.NET API on 127.0.0.1:5280). Only the paths the desktop app needs;
# everything else under /api/ still goes to Node.
location = /api/health          { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location = /api/activation      { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/activation/    { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/registration/  { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
location ^~ /api/licence/       { proxy_pass http://127.0.0.1:5280; include /www/server/nginx/conf/lonnii-api-proxy.conf; }
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
  echo "nginx test FAILED - the original site file was restored. Nothing changed."
  nginx -t || true
  exit 1
fi

echo
echo -n "licence server health : "; curl -s https://www.lonnii.com/api/health || echo "(no answer)"
echo
echo -n "React site            : "; curl -s -o /dev/null -w "%{http_code}\n" https://www.lonnii.com/
