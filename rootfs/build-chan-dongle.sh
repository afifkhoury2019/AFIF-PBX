#!/usr/bin/env bash
# Build chan_dongle against the exact Asterisk that the FreePBX installer put on the system.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

AST_FULL=$(asterisk -V | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1)
AST_MAJOR=${AST_FULL%%.*}
echo "Asterisk $AST_FULL"

# Where Asterisk keeps its modules (Sangoma packages use the multiarch path)
MODDIR=$(dirname "$(find /usr/lib /usr/lib64 -name chan_pjsip.so 2>/dev/null | head -1)")
[ -d "$MODDIR" ] || { echo "cannot find Asterisk module dir"; exit 1; }
echo "module dir: $MODDIR"

INC=""
# 1) Prefer the matching development package if the Sangoma repo has one.
if apt-get install -y "asterisk${AST_MAJOR}-devel" 2>/dev/null && [ -f /usr/include/asterisk/version.h ]; then
  INC=/usr/include
  echo "using asterisk${AST_MAJOR}-devel headers"
else
  # 2) Otherwise use the headers from the identical upstream source release.
  cd /usr/src
  T=asterisk-$AST_FULL
  if [ ! -d "$T" ]; then
    wget -q "https://downloads.asterisk.org/pub/telephony/asterisk/releases/$T.tar.gz" \
      || wget -q "https://downloads.asterisk.org/pub/telephony/asterisk/old-releases/$T.tar.gz"
    tar xzf "$T.tar.gz"
  fi
  cd "$T"
  ./configure --without-pjproject-bundled >/tmp/ast-configure.log 2>&1 \
    || { tail -30 /tmp/ast-configure.log; ./configure >/tmp/ast-configure.log 2>&1; }
  # Empty build-option checksum: the loader skips the option comparison for this module.
  cat > include/asterisk/buildopts.h <<'EOF'
#ifndef _ASTERISK_BUILDOPTS_H
#define _ASTERISK_BUILDOPTS_H
#define AST_BUILDOPT_SUM ""
#endif
EOF
  INC=/usr/src/$T/include
  echo "using source headers $INC"
fi

cd /usr/src
rm -rf asterisk-chan-dongle
git clone --depth 1 https://github.com/wdoekes/asterisk-chan-dongle.git
cd asterisk-chan-dongle
./bootstrap
./configure --with-astversion="$AST_FULL" --with-asterisk="$INC" DESTDIR="$MODDIR"
make -j"$(nproc)"
install -m 0755 chan_dongle.so "$MODDIR/chan_dongle.so"
# keep the sample for reference
install -D -m 0644 etc/dongle.conf /usr/share/doc/afif-pbx/dongle.conf.sample
ls -l "$MODDIR/chan_dongle.so"
