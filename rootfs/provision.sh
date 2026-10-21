#!/usr/bin/env bash
# Runs INSIDE the Debian 12 systemd build container.
# Installs FreePBX 17 (open-source modules only) + Asterisk 22, chan_dongle for the
# Huawei E303, WSL integration, restores the Raspberry Pi configuration (if a bundle
# was supplied) and applies the AFIF-PBX branding.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
D=/opt/afif-pbx
LOG=/var/log/afif-pbx-build.log
step(){ echo; echo "=================== $* ==================="; }

step "Base packages"
apt-get update
apt-get install -y --no-install-recommends \
  build-essential autoconf automake libtool pkg-config git \
  usb-modeswitch usb-modeswitch-data usbutils kmod udev \
  libsqlite3-dev libssl-dev libjansson-dev libxml2-dev uuid-dev libedit-dev libncurses-dev \
  python3 net-tools

step "FreePBX 17 + Asterisk 22 (official Sangoma installer, open-source modules only)"
wget -qO /tmp/sng_freepbx_debian_install.sh \
  https://github.com/FreePBX/sng_freepbx_debian_install/raw/master/sng_freepbx_debian_install.sh
# The installer wants an FQDN
grep -q 'afif-pbx.local' /etc/hosts || echo "127.0.1.1 afif-pbx.local afif-pbx" >> /etc/hosts
set +e
bash /tmp/sng_freepbx_debian_install.sh --opensourceonly --noaac --nochrony --skipversion
rc=$?
set -e
echo "installer exit code: $rc"
tail -n 60 /var/log/pbx/freepbx17-install.log || true
command -v fwconsole >/dev/null || { echo "FreePBX did not install"; exit 1; }
asterisk -V

step "chan_dongle (Huawei E303 voice/SMS)"
bash $D/rootfs/build-chan-dongle.sh

step "WSL integration"
install -m 0644 $D/rootfs/files/wsl.conf /etc/wsl.conf
install -m 0755 $D/rootfs/files/afif-keepalive /usr/local/bin/afif-keepalive
install -m 0755 $D/rootfs/files/afif-dongle /usr/local/bin/afif-dongle
install -m 0755 $D/rootfs/files/afif-status /usr/local/bin/afif-status
install -m 0644 $D/rootfs/files/99-afif-e303.rules /etc/udev/rules.d/99-afif-e303.rules
install -m 0644 $D/rootfs/files/afif-dongle.service /etc/systemd/system/afif-dongle.service
install -m 0644 $D/rootfs/files/modules-afif.conf /etc/modules-load.d/afif-pbx.conf
usermod -aG dialout asterisk
systemctl enable afif-dongle.service
# Services that make no sense inside WSL
systemctl disable --now tftpd-hpa 2>/dev/null || true
systemctl mask systemd-networkd-wait-online.service 2>/dev/null || true

if [ -d $D/restore/fs ]; then
  step "Restoring Raspberry Pi configuration"
  bash $D/rootfs/restore-config.sh $D/restore
else
  step "No restore bundle — default chan_dongle config"
  [ -f /etc/asterisk/dongle.conf ] || install -o asterisk -g asterisk -m 0640 $D/rootfs/files/dongle.conf /etc/asterisk/dongle.conf
fi

step "AFIF-PBX branding"
bash $D/branding/apply-branding.sh

step "Final reload"
fwconsole chown >/dev/null
fwconsole reload
fwconsole restart
sleep 5
asterisk -rx "module show like dongle"
echo "provision OK"
