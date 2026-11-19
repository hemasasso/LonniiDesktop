#!/usr/bin/env bash
# STEP 2 of 3 - on OCI, as root:  bash setup-aws-mirror-access.sh 'ssh-ed25519 AAAA... aws-oci-mirror'
# Lets the AWS server (and only it, from 34.232.44.192, with that key) READ the backups each night:
#   /var/backups/lonnii/            database dump + keys
#   /var/lib/lonnii-api/backups/    shops' cloud backups
# and leave a one-line status the morning email reports. It cannot write, delete, or open a shell.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }
PUBKEY="${1:-}"
[[ "$PUBKEY" == ssh-ed25519\ * ]] || { echo "Usage: bash $0 'ssh-ed25519 AAAA... aws-oci-mirror'"; exit 1; }
AWS_IP=34.232.44.192
U=lonnii_mirror

command -v rsync >/dev/null || dnf -y -q install rsync

id "$U" >/dev/null 2>&1 || useradd -r -m -d /home/$U -s /bin/bash "$U"
install -d -m 750 -o "$U" -g "$U" /var/lib/lonnii-mirror

# Runs as root (through sudo): accepts only a read-only rsync of the two backup folders.
cat > /usr/local/sbin/lonnii-mirror-serve <<'EOF'
#!/bin/bash
set -u
cmd="$1"
case "$cmd" in *[\;\&\|\`\$\(\)\<\>\\]*|*remove*) echo "refused" >&2; exit 1 ;; esac
read -r -a w <<< "$cmd"
n=${#w[@]}
[ "$n" -ge 5 ] && [ "${w[0]}" = rsync ] && [ "${w[1]}" = --server ] && [ "${w[2]}" = --sender ] || { echo "refused" >&2; exit 1; }
case "${w[$((n-1))]}" in /var/backups/lonnii/|/var/lib/lonnii-api/backups/) ;; *) echo "refused" >&2; exit 1 ;; esac
[ "${w[$((n-2))]}" = . ] || { echo "refused" >&2; exit 1; }
exec /usr/bin/rsync "${w[@]:1}"
EOF
chmod 700 /usr/local/sbin/lonnii-mirror-serve

# Runs as lonnii_mirror for every connection from AWS: a status line, or the read-only copy above.
cat > /usr/local/sbin/lonnii-mirror-gate <<'EOF'
#!/bin/bash
set -u
c="${SSH_ORIGINAL_COMMAND:-}"
if [[ "$c" == "report-status "* ]]; then
  printf '%s %s\n' "$(date -u +%FT%TZ)" "$(printf '%s' "${c#report-status }" | tr -cd 'A-Za-z0-9 ._:%/-' | cut -c1-300)" \
    > /var/lib/lonnii-mirror/aws-status
  exit 0
fi
exec sudo -n /usr/local/sbin/lonnii-mirror-serve "$c"
EOF
chmod 755 /usr/local/sbin/lonnii-mirror-gate

echo "$U ALL=(root) NOPASSWD: /usr/local/sbin/lonnii-mirror-serve" > /etc/sudoers.d/lonnii-mirror
chmod 440 /etc/sudoers.d/lonnii-mirror
visudo -cf /etc/sudoers.d/lonnii-mirror >/dev/null

install -d -m 700 -o "$U" -g "$U" /home/$U/.ssh
echo "restrict,from=\"$AWS_IP\",command=\"/usr/local/sbin/lonnii-mirror-gate\" $PUBKEY" > /home/$U/.ssh/authorized_keys
chown "$U:$U" /home/$U/.ssh/authorized_keys; chmod 600 /home/$U/.ssh/authorized_keys

# sshd only lets in the users on AllowUsers.
if ! grep -qE "^AllowUsers.*\b$U\b" /etc/ssh/sshd_config; then
  cp -a /etc/ssh/sshd_config "/etc/ssh/sshd_config.bak-$(date +%F-%H%M)"
  sed -i -E "s/^(AllowUsers .*)$/\1 $U/" /etc/ssh/sshd_config
fi
sshd -t && systemctl reload sshd

# fail2ban must never ban the AWS server.
J=/etc/fail2ban/jail.d/sshd.local
if [ -f "$J" ] && ! grep -q "$AWS_IP" "$J"; then
  sed -i -E "s/^(ignoreip *=.*)$/\1 $AWS_IP/" "$J" && systemctl reload fail2ban || true
fi

# The morning email learns to report AWS's copy (same folder as this script).
if [ -f "$(dirname "$0")/lonnii-daily-report.sh" ]; then
  install -m 700 -o root -g root "$(dirname "$0")/lonnii-daily-report.sh" /usr/local/sbin/lonnii-daily-report.sh
  echo "Morning report updated."
fi

echo
echo "OCI fingerprint (should match step 1):"; ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
echo "Size AWS will copy the first night:"; du -sh /var/backups/lonnii /var/lib/lonnii-api/backups 2>/dev/null
echo
echo "=== STEP 3: on AWS, run once:  sudo /usr/local/sbin/lonnii-oci-mirror.sh ==="
