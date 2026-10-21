#!/usr/bin/env bash
# AFIF-PBX branding for the FreePBX web interface.
# Safe to run again any time (e.g. after module updates):  afif-rebrand
set -euo pipefail
SRC=$(cd "$(dirname "$0")" && pwd)
[ -f "$SRC/afif-pbx.css" ] || SRC=/opt/afif-pbx/branding
WEB=$(mysql -N asterisk -e "SELECT value FROM freepbx_settings WHERE keyword='AMPWEBROOT'" 2>/dev/null || echo /var/www/html)
WEB=${WEB:-/var/www/html}
A=$WEB/admin/assets/afif
mkdir -p "$A"

command -v rsvg-convert >/dev/null || apt-get install -y --no-install-recommends librsvg2-bin imagemagick >/dev/null
command -v convert >/dev/null || apt-get install -y --no-install-recommends imagemagick >/dev/null

# ---- logo: use the supplied PNG, otherwise render the SVG ----
if [ -f "$SRC/logo.png" ]; then
  cp "$SRC/logo.png" "$A/logo.png"
else
  rsvg-convert -w 512 "$SRC/logo.svg" -o "$A/logo.png"
fi
[ -f "$SRC/logo.svg" ] && cp "$SRC/logo.svg" "$A/logo.svg"
convert "$A/logo.png" -resize x60 "$A/logo-header.png"
convert "$A/logo.png" -resize x40 "$A/logo-footer.png"
convert "$A/logo.png" -resize 256x256 -background none -gravity center -extent 256x256 \
        -define icon:auto-resize=64,48,32,16 "$A/favicon.ico"
cp "$SRC/afif-pbx.css" "$SRC/afif-pbx.js" "$A/"

# Replace stock logo files too (used on pages that do not read the branding settings)
for img in freepbx.png freepbx_small.png tango.png; do
  f=$WEB/admin/images/$img
  [ -f "$f" ] && [ ! -f "$f.orig" ] && cp "$f" "$f.orig" || true
  [ -f "$f" ] && convert "$A/logo.png" -resize "$(identify -format '%wx%h' "$f.orig" 2>/dev/null || echo x60)" "$f" || true
done
for img in sangoma-horizontal_thumb.png schmooze-logo.png; do
  f=$WEB/admin/images/$img
  [ -f "$f" ] && [ ! -f "$f.orig" ] && cp "$f" "$f.orig" && cp "$A/logo-footer.png" "$f" || true
done
cp "$A/favicon.ico" "$WEB/admin/images/favicon.ico"
cp "$A/favicon.ico" "$WEB/favicon.ico"

# ---- FreePBX branding settings ----
set_conf(){
  local v; v=$(printf %s "$2" | sed "s/'/''/g")
  mysql asterisk -e "UPDATE freepbx_settings SET value='$v' WHERE keyword='$1'"
}
set_conf BRAND_TITLE                   "AFIF-PBX"
set_conf BRAND_IMAGE_TANGO_LEFT        "assets/afif/logo-header.png"
set_conf BRAND_FREEPBX_ALT_LEFT        "AFIF-PBX"
set_conf BRAND_IMAGE_FREEPBX_LINK_LEFT "config.php"
set_conf BRAND_IMAGE_FAVICON           "assets/afif/favicon.ico"
set_conf BRAND_IMAGE_FREEPBX_FOOT      "assets/afif/logo-footer.png"
set_conf BRAND_FREEPBX_ALT_FOOT        "AFIF-PBX"
set_conf BRAND_IMAGE_FREEPBX_LINK_FOOT "config.php"
set_conf BRAND_IMAGE_SPONSOR_FOOT      "assets/afif/logo-footer.png"
set_conf BRAND_SPONSOR_ALT_FOOT        "AFIF-TECH"
set_conf BRAND_IMAGE_SPONSOR_LINK_FOOT "config.php"
set_conf BRAND_CSS_CUSTOM              "assets/afif/afif-pbx.css"
set_conf BRAND_ALT_JS                  "assets/afif/afif-pbx.js"
set_conf FREEPBX_SYSTEM_IDENT          "AFIF-PBX"
set_conf DASHBOARD_FREEPBX_BRAND       "AFIF-PBX"

# ---- animated landing page at http://<pc>/ ----
install -m 0644 "$SRC/index_custom.php" "$WEB/index_custom.php"
cp "$SRC/landing.css" "$A/landing.css"

# ---- replace the remaining visible "FreePBX" words in HTML pages ----
a2enmod substitute headers >/dev/null
cat > /etc/apache2/conf-available/afif-pbx-branding.conf <<'EOF'
# AFIF-PBX: show "AFIF-PBX" wherever the stock interface prints "FreePBX" as text.
# Only text/html is touched, and only whole words that are not JavaScript/PHP identifiers.
<Location "/">
    SetEnv no-gzip 1
    RequestHeader unset Accept-Encoding
    AddOutputFilterByType SUBSTITUTE text/html
    Substitute "s|(?<![\w.$/-])FreePBX(?![\w.\[(:/-])|AFIF-PBX|"
    Substitute "s|Sangoma Technologies|AFIF-TECH|"
</Location>
EOF
a2enconf afif-pbx-branding >/dev/null
apachectl configtest
systemctl reload apache2 || systemctl restart apache2

chown -R asterisk:asterisk "$A" "$WEB/index_custom.php" 2>/dev/null || true
fwconsole reload >/dev/null 2>&1 || true
echo "AFIF-PBX branding applied"
