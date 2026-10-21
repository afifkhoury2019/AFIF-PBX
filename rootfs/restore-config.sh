#!/usr/bin/env bash
# Restore the Raspberry Pi PBX (extracted by image-extract/extract.sh) onto this FreePBX 17 system.
# Usage: restore-config.sh <bundle-dir>
set -euo pipefail
B=${1:?bundle}
FS=$B/fs
echo "Source system:"; cat "$B/meta/os-release" 2>/dev/null | grep PRETTY || true
echo "Source FreePBX: $(cat "$B/meta/freepbx-version" 2>/dev/null)  Asterisk: $(cat "$B/meta/asterisk-version" 2>/dev/null)"

fwconsole stop >/dev/null 2>&1 || true

# Paths and DB settings that belong to THIS machine and must survive the database import
KEEP="AMPWEBROOT AMPBIN AMPSBIN ASTETCDIR ASTMODDIR ASTVARLIBDIR ASTAGIDIR ASTSPOOLDIR ASTRUNDIR ASTLOGDIR ASTDATADIR CERTKEYLOC AMPCGIBIN ASTVERSION AMPPLAYBACK AMPEXTENSIONS"
mysql -N asterisk -e "SELECT keyword, value FROM freepbx_settings WHERE keyword IN ('$(echo $KEEP | sed "s/ /','/g")')" > /tmp/keep-settings.tsv

# ---------- database ----------
if ls "$B"/db/*.sql >/dev/null 2>&1; then
  for f in "$B"/db/*.sql; do
    db=$(basename "$f" .sql)
    echo "Importing database $db ($(du -h "$f" | cut -f1))"
    mysql -e "DROP DATABASE IF EXISTS \`$db\`"
    # Strip DEFINER clauses that name users that do not exist here
    sed -E 's/DEFINER=`[^`]+`@`[^`]+`//g' "$f" | mysql
  done
  # Re-grant the FreePBX 17 database user on the imported databases
  DBUSER=$(php -r 'include "/etc/freepbx.conf"; echo $amp_conf["AMPDBUSER"];' 2>/dev/null || true)
  if [ -n "$DBUSER" ]; then
    for db in asterisk asteriskcdrdb; do mysql -e "GRANT ALL PRIVILEGES ON \`$db\`.* TO '$DBUSER'@'localhost'" || true; done
    mysql -e "FLUSH PRIVILEGES"
  fi
  # Put this machine's paths back
  while IFS=$'\t' read -r k v; do
    mysql asterisk -e "UPDATE freepbx_settings SET value='$(printf %s "$v" | sed "s/'/''/g")' WHERE keyword='$k'"
  done < /tmp/keep-settings.tsv
else
  echo "No database dump in bundle — configuration files only"
fi

# ---------- Asterisk files ----------
if [ -d "$FS/etc/asterisk" ]; then
  cd "$FS/etc/asterisk"
  for f in $(find . -type f | sed 's:^\./::'); do
    dst=/etc/asterisk/$f
    case "$f" in
      *_custom.conf|*_custom_post.conf|*_custom.d/*|dongle.conf|*override*.conf)
        install -D -o asterisk -g asterisk -m 0640 "$f" "$dst"; echo "  restored $f" ;;
      *)
        # anything FreePBX does not generate and that does not exist yet (e.g. extra module configs)
        if [ ! -e "$dst" ]; then install -D -o asterisk -g asterisk -m 0640 "$f" "$dst"; echo "  added $f"; fi ;;
    esac
  done
  cd - >/dev/null
fi

copydir(){ [ -d "$FS/$1" ] && mkdir -p "/$1" && cp -a "$FS/$1/." "/$1/" && echo "  restored /$1"; true; }
copydir var/lib/asterisk/sounds/custom
copydir var/lib/asterisk/sounds/en/custom
copydir var/lib/asterisk/moh
copydir var/lib/asterisk/agi-bin
copydir var/lib/asterisk/keys
copydir var/spool/asterisk/voicemail
copydir var/spool/asterisk/fax
copydir etc/usb_modeswitch.d
[ -f "$FS/etc/usb_modeswitch.conf" ] && cp -a "$FS/etc/usb_modeswitch.conf" /etc/usb_modeswitch.conf
[ -f "$FS/var/lib/asterisk/astdb.sqlite3" ] && install -o asterisk -g asterisk -m 0640 "$FS/var/lib/asterisk/astdb.sqlite3" /var/lib/asterisk/astdb.sqlite3 && echo "  restored astdb"

# Pi-specific dongle udev rules (symlinks like /dev/dongle0) are worth keeping
if [ -d "$FS/etc/udev/rules.d" ]; then
  for r in "$FS"/etc/udev/rules.d/*; do
    [ -f "$r" ] || continue
    grep -qiE '12d1|ttyUSB|dongle|huawei' "$r" && cp "$r" /etc/udev/rules.d/ && echo "  udev rule $(basename "$r")"
  done
fi

# chan_dongle on the Pi may point at Pi-only paths
if [ -f /etc/asterisk/dongle.conf ]; then
  sed -i -E 's#^(smsdb\s*=).*#\1/var/lib/asterisk/smsdb#' /etc/asterisk/dongle.conf
fi

# ---------- modules ----------
fwconsole chown >/dev/null 2>&1 || true
systemctl start mariadb
fwconsole start >/dev/null 2>&1 || true
echo "Upgrading restored module data to FreePBX 17"
fwconsole ma installlocal || true
fwconsole ma upgradeall || true
# Modules that were enabled on the Pi but are not on disk here: try to fetch the open-source ones
if [ -s "$B/meta/q/modules.tsv" ]; then
  have=$(ls /var/www/html/admin/modules)
  tail -n +2 "$B/meta/q/modules.tsv" | awk -F'\t' '$3==1 {print $1}' | while read -r m; do
    echo "$have" | grep -qx "$m" && continue
    echo "  fetching module $m"
    fwconsole ma downloadinstall "$m" >/dev/null 2>&1 && echo "    ok" || echo "    $m not available (commercial or retired) — skipped" | tee -a /var/log/afif-pbx-restore-skipped.log
  done
fi
fwconsole chown >/dev/null
fwconsole reload
echo "Restore complete"
