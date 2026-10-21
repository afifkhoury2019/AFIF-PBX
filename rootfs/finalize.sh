#!/usr/bin/env bash
# Last step before the container filesystem is exported as the WSL distro.
set -e
# Restore bundle (database dumps etc.) is not needed at runtime
rm -rf /opt/afif-pbx/restore
# Keep the helper scripts and branding for re-applying later (afif-rebrand)
install -m 0755 /opt/afif-pbx/branding/apply-branding.sh /usr/local/bin/afif-rebrand
# Docker-specific files that WSL regenerates
rm -f /.dockerenv || true
echo afif-pbx > /etc/hostname 2>/dev/null || true
apt-get clean
rm -rf /var/lib/apt/lists/* /usr/src/asterisk-*.tar.gz /tmp/* /var/tmp/*
find /var/log -type f -name '*.log' -size +1M -exec truncate -s 0 {} \;
# Machine identity is created on first boot in WSL
: > /etc/machine-id
echo "finalized"
