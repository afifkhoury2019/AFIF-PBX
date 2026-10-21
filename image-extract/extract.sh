#!/usr/bin/env bash
# AFIF-PBX — extract a Raspberry Pi FreePBX/RasPBX disk image into a restore bundle.
# Usage: sudo image-extract/extract.sh <image.img> <output-dir>
# Produces:
#   <out>/bundle/            files copied from the image (config, sounds, voicemail ...)
#   <out>/bundle/db/*.sql    mysqldump of the asterisk + asteriskcdrdb databases
#   <out>/bundle/meta/*      versions and facts used by the restore step
set -euo pipefail

IMG=${1:?image path}
OUT=${2:?output dir}
HERE=$(cd "$(dirname "$0")" && pwd)
B="$OUT/bundle"
MNT=$(mktemp -d)
mkdir -p "$B/meta" "$B/db" "$B/fs"

log(){ echo "::group::$*" >/dev/null 2>&1 || true; echo "[extract] $*"; }

cleanup(){
  set +e
  docker rm -f afif-mysql-dump >/dev/null 2>&1
  for m in $(mount | awk -v p="$MNT" '$3 ~ "^"p {print $3}' | sort -r); do umount -l "$m"; done
  [ -n "${LOOP:-}" ] && losetup -d "$LOOP"
  rm -rf "$MNT"
}
trap cleanup EXIT

log "Attaching image $IMG ($(du -h "$IMG" | cut -f1))"
LOOP=$(losetup -Pf --show "$IMG")
partprobe "$LOOP" 2>/dev/null || true
sleep 2
lsblk -o NAME,SIZE,FSTYPE,LABEL "$LOOP" | tee "$B/meta/partitions.txt"

# Find the Linux root partition: the one that contains /etc/os-release.
ROOT=""
for p in "${LOOP}"p*; do
  fstype=$(blkid -o value -s TYPE "$p" 2>/dev/null || true)
  [ "$fstype" = "ext4" ] || [ "$fstype" = "ext3" ] || [ "$fstype" = "ext2" ] || continue
  mkdir -p "$MNT/probe"
  if mount -o ro,noload "$p" "$MNT/probe" 2>/dev/null; then
    if [ -f "$MNT/probe/etc/os-release" ]; then ROOT=$p; fi
    umount "$MNT/probe"
  fi
  [ -n "$ROOT" ] && break
done
[ -n "$ROOT" ] || { echo "No Linux root partition found in image"; exit 1; }
mkdir -p "$MNT/root"
mount -o ro,noload "$ROOT" "$MNT/root"
R="$MNT/root"
log "Root filesystem: $ROOT"

# ---------- facts ----------
cp "$R/etc/os-release" "$B/meta/os-release"
cat "$R/etc/hostname" > "$B/meta/hostname" 2>/dev/null || true
uname_arch=$(file -L "$R/bin/bash" | sed -n 's/.*ELF \([0-9]*-bit\).*, \([^,]*\),.*/\1 \2/p')
echo "$uname_arch" > "$B/meta/arch"

# Asterisk version: ask the binary's embedded string.
AST_BIN=""
for c in "$R/usr/sbin/asterisk" "$R/usr/local/sbin/asterisk"; do [ -x "$c" ] && AST_BIN=$c && break; done
if [ -n "$AST_BIN" ]; then
  strings "$AST_BIN" | grep -m1 -E '^[0-9]+\.[0-9]+\.[0-9]+' > "$B/meta/asterisk-version" || true
  [ -s "$B/meta/asterisk-version" ] || strings "$AST_BIN" | grep -m1 -oE 'Asterisk [0-9]+\.[0-9.]+' > "$B/meta/asterisk-version" || true
fi

WEBROOT=""
for w in "$R/var/www/html" "$R/var/www"; do [ -d "$w/admin" ] && WEBROOT=$w && break; done
if [ -n "$WEBROOT" ] && [ -f "$WEBROOT/admin/modules/framework/module.xml" ]; then
  sed -n 's:.*<version>\(.*\)</version>.*:\1:p' "$WEBROOT/admin/modules/framework/module.xml" | head -1 > "$B/meta/freepbx-version"
fi

# chan_dongle present?
find "$R/usr/lib" "$R/usr/local/lib" -name 'chan_dongle.so' 2>/dev/null | head -1 > "$B/meta/chan_dongle-path" || true

# ---------- files ----------
copy(){ # copy path relative to image root, if it exists
  local rel=$1
  if [ -e "$R/$rel" ]; then
    mkdir -p "$B/fs/$(dirname "$rel")"
    cp -a "$R/$rel" "$B/fs/$rel"
    echo "$rel" >> "$B/meta/copied.txt"
  fi
}
for p in \
  etc/asterisk etc/freepbx.conf etc/amportal.conf etc/asterisk/freepbx.conf \
  etc/usb_modeswitch.conf etc/usb_modeswitch.d etc/udev/rules.d \
  etc/dhcpcd.conf etc/network/interfaces etc/hosts etc/timezone etc/crontab \
  var/spool/cron/crontabs \
  var/lib/asterisk/agi-bin var/lib/asterisk/sounds/custom var/lib/asterisk/sounds/en/custom \
  var/lib/asterisk/moh var/lib/asterisk/keys var/lib/asterisk/astdb.sqlite3 \
  var/lib/asterisk/bin \
  var/spool/asterisk/voicemail var/spool/asterisk/fax \
  etc/fail2ban/jail.local etc/postfix/main.cf etc/postfix/sasl_passwd \
  etc/ssmtp/ssmtp.conf etc/msmtprc
do copy "$p"; done

# Size of call recordings (not copied by default — can be large)
du -sh "$R/var/spool/asterisk/monitor" 2>/dev/null > "$B/meta/monitor-size" || true

# Custom modules list on disk
[ -n "$WEBROOT" ] && ls "$WEBROOT/admin/modules" > "$B/meta/modules-on-disk.txt" 2>/dev/null || true

# ---------- database ----------
DATADIR=""
for d in "$R/var/lib/mysql"; do [ -d "$d/asterisk" ] && DATADIR=$d; done
if [ -n "$DATADIR" ]; then
  log "Dumping MariaDB/MySQL databases"
  ver=$(head -1 "$DATADIR/mysql_upgrade_info" 2>/dev/null | grep -oE '^[0-9]+\.[0-9]+' || true)
  flavour=mariadb
  if [ -z "$ver" ]; then
    # MySQL (not MariaDB) or no upgrade info: try the binary
    if [ -x "$R/usr/sbin/mysqld" ]; then
      v=$(strings "$R/usr/sbin/mysqld" | grep -m1 -oE '[0-9]+\.[0-9]+\.[0-9]+-MariaDB' || true)
      ver=$(echo "$v" | grep -oE '^[0-9]+\.[0-9]+' || true)
    fi
  fi
  ver=${ver:-10.11}
  echo "$flavour $ver" > "$B/meta/db-server"
  WORK=$(mktemp -d)
  cp -a "$DATADIR/." "$WORK/"
  rm -f "$WORK"/*.pid "$WORK"/mysql.sock "$WORK"/aria_log_control 2>/dev/null || true
  chown -R 999:999 "$WORK"
  # Try the exact version first, then newer ones (they can read older data dirs).
  ok=0
  for tag in "$ver" 10.11 11.4; do
    docker rm -f afif-mysql-dump >/dev/null 2>&1 || true
    echo "[extract] trying mariadb:$tag"
    docker run -d --name afif-mysql-dump -v "$WORK:/var/lib/mysql" "mariadb:$tag" \
      --skip-grant-tables --skip-networking=0 --innodb-force-recovery=0 >/dev/null || continue
    for i in $(seq 1 60); do
      docker exec afif-mysql-dump sh -c 'mariadb-admin ping 2>/dev/null || mysqladmin ping 2>/dev/null' | grep -q alive && break
      sleep 2
    done
    DBS=$(docker exec afif-mysql-dump sh -c 'mariadb -N -e "show databases" 2>/dev/null || mysql -N -e "show databases"' | grep -vE '^(information_schema|performance_schema|mysql|sys)$' || true)
    if [ -n "$DBS" ]; then
      echo "$DBS" > "$B/meta/databases.txt"
      for db in $DBS; do
        docker exec afif-mysql-dump sh -c "mariadb-dump --single-transaction --routines --triggers --databases $db 2>/dev/null || mysqldump --single-transaction --routines --triggers --databases $db" > "$B/db/$db.sql"
        echo "[extract]   $db: $(du -h "$B/db/$db.sql" | cut -f1)"
      done
      # Query the facts the inventory and credentials report need (TSV, one file per query).
      mkdir -p "$B/meta/q"
      q(){ docker exec afif-mysql-dump sh -c "mariadb -B asterisk -e \"$2\" 2>/dev/null || mysql -B asterisk -e \"$2\" 2>/dev/null" > "$B/meta/q/$1.tsv" || true; }
      q ampusers      "SELECT username, password_sha1, sections FROM ampusers"
      q users         "SELECT extension, name, voicemail, outboundcid FROM users ORDER BY CAST(extension AS UNSIGNED)"
      q devices       "SELECT id, tech, dial, description FROM devices ORDER BY CAST(id AS UNSIGNED)"
      q sip           "SELECT id, keyword, data FROM sip WHERE keyword IN ('secret','account','dial','context','host','username','fromuser','sipdriver')"
      q pjsip         "SELECT id, keyword, data FROM pjsip WHERE keyword IN ('secret','username','auth_username','sip_server','server_uri','client_uri','contact_user','registration','transport','fromdomain','from_domain','outbound_proxy')"
      q iax           "SELECT id, keyword, data FROM iax WHERE keyword IN ('secret','account','host','username')"
      q trunks        "SELECT trunkid, name, tech, channelid, outcid, disabled FROM trunks"
      q trunk_dialpats "SELECT trunkid, match_pattern_prefix, match_pattern_pass, prepend_digits FROM trunk_dialpatterns"
      q incoming      "SELECT extension AS did, cidnum, destination, description FROM incoming"
      q outbound_routes "SELECT r.route_id, r.name, GROUP_CONCAT(t.trunk_id ORDER BY t.seq) AS trunks FROM outbound_routes r LEFT JOIN outbound_route_trunks t ON t.route_id=r.route_id GROUP BY r.route_id, r.name"
      q route_patterns "SELECT route_id, match_pattern_prefix, match_pattern_pass, prepend_digits FROM outbound_route_patterns"
      q ringgroups    "SELECT grpnum, description, grplist, strategy FROM ringgroups"
      q ivr           "SELECT id, name, description FROM ivr_details"
      q queues        "SELECT extension, descr FROM queues_config"
      q timeconditions "SELECT timeconditions_id, displayname FROM timeconditions"
      q modules       "SELECT modulename, version, enabled FROM modules ORDER BY modulename"
      q settings      "SELECT keyword, value FROM freepbx_settings WHERE keyword IN ('AMPMGRUSER','AMPMGRPASS','ASTVERSION','AMPWEBROOT','ASTETCDIR','HTTPBINDPORT','BRAND_TITLE','FREEPBX_SYSTEM_IDENT')"
      q customdests   "SELECT custom_dest, description FROM custom_destinations"
      ok=1; break
    fi
    docker logs afif-mysql-dump 2>&1 | tail -20
  done
  docker rm -f afif-mysql-dump >/dev/null 2>&1 || true
  rm -rf "$WORK"
  [ "$ok" = 1 ] || echo "WARNING: database dump failed" | tee "$B/meta/db-dump-failed"
else
  echo "[extract] No MySQL data directory with an 'asterisk' database found"
  # Some RasPBX builds keep everything in sqlite
  find "$R/var/lib" -maxdepth 3 -name '*.sqlite*' 2>/dev/null >> "$B/meta/sqlite-files.txt" || true
fi

# ---------- reports ----------
python3 -I "$HERE/inventory.py" "$B" "$OUT"
echo "[extract] done"
