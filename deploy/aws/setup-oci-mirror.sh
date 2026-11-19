#!/usr/bin/env bash
# STEP 1 of 3 - on AWS, as root:  sudo bash setup-oci-mirror.sh
# Installs the nightly copy of OCI's backups (/usr/local/sbin/lonnii-oci-mirror.sh, cron every 3 h at hh:45 UTC)
# and prints the key to paste into OCI's setup script (step 2). Step 3 = first run by hand.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root (sudo)."; exit 1; }
cd "$(dirname "$0")"
OCI=150.136.9.41

command -v rsync >/dev/null || { apt-get update -qq && apt-get install -y -qq rsync; }
[ -x /usr/lib/postgresql/16/bin/pg_restore ] || { echo "pg_restore 16 not found (needed to check each dump)."; exit 1; }

install -d -m 700 /root/.ssh
[ -f /root/.ssh/oci_mirror ] || ssh-keygen -q -t ed25519 -N '' -C "aws-oci-mirror" -f /root/.ssh/oci_mirror

# Remember OCI's identity, so the copy refuses any other machine later.
touch /root/.ssh/known_hosts; chmod 600 /root/.ssh/known_hosts
ssh-keygen -F "$OCI" -f /root/.ssh/known_hosts >/dev/null || ssh-keyscan -t ed25519 "$OCI" 2>/dev/null >> /root/.ssh/known_hosts

install -m 700 -o root -g root lonnii-oci-mirror.sh /usr/local/sbin/lonnii-oci-mirror.sh
cat > /etc/cron.d/lonnii-oci-mirror <<'EOF'
# Copy of OCI's backups (client database, keys, shops' cloud backups) - see /usr/local/sbin/lonnii-oci-mirror.sh
45 2,5,8,11,14,17,20,23 * * * root /usr/local/sbin/lonnii-oci-mirror.sh >> /var/log/lonnii-oci-mirror.log 2>&1
EOF
chmod 644 /etc/cron.d/lonnii-oci-mirror

echo
echo "OCI fingerprint seen from here (compare with step 2's output):"
ssh-keygen -lf /root/.ssh/known_hosts -F "$OCI" | grep -v '^#' || true
echo
echo "=== STEP 2: on OCI, as root, run:  bash setup-aws-mirror-access.sh '<the line below>' ==="
cat /root/.ssh/oci_mirror.pub
