#!/usr/bin/env bash
# Installs the Lonnii API as a systemd service (run as root, on the OCI server).
# Reads its settings from /etc/lonnii-api.env. Safe to run again.
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }
[ -x /opt/lonnii-api/Lonnii.Api ] || { echo "/opt/lonnii-api/Lonnii.Api is missing or not executable."; exit 1; }
[ -f /etc/lonnii-api.env ] || { echo "/etc/lonnii-api.env is missing."; exit 1; }

cat > /etc/systemd/system/lonnii-api.service <<'UNIT'
[Unit]
Description=Lonnii API (licence server)
After=network.target postgresql-16.service

[Service]
User=lonnii
WorkingDirectory=/opt/lonnii-api
ExecStart=/opt/lonnii-api/Lonnii.Api
EnvironmentFile=/etc/lonnii-api.env
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable lonnii-api
systemctl restart lonnii-api
sleep 5

echo "service: $(systemctl is-active lonnii-api)"
echo -n "health : "
curl -s http://127.0.0.1:5280/api/health || echo "(no answer - see: journalctl -u lonnii-api -n 40)"
echo
