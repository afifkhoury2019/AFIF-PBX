#!/usr/bin/env bash
# Build-time checks. Any failure fails the CI job.
set -uo pipefail
fail=0
check(){ local name=$1; shift; if "$@" >/tmp/selftest.out 2>&1; then echo "PASS  $name"; else echo "FAIL  $name"; cat /tmp/selftest.out; fail=1; fi; }

check "asterisk running"          asterisk -rx "core show version"
check "chan_dongle loaded"        bash -c 'asterisk -rx "module show like chan_dongle" | grep -q "chan_dongle.so"'
check "dongle CLI available"      bash -c 'asterisk -rx "dongle show version" | grep -qi version'
check "dongle.conf present"       test -f /etc/asterisk/dongle.conf
check "pjsip/sip channel driver"  bash -c 'asterisk -rx "module show like chan_" | grep -qE "chan_pjsip|chan_sip"'
check "web UI answers"            bash -c 'curl -sf -o /dev/null http://127.0.0.1/admin/config.php || curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1/admin/ | grep -qE "200|302"'
check "AFIF-PBX title"            bash -c 'fwconsole setting BRAND_TITLE | grep -q "AFIF-PBX"'
check "branding assets"           test -f /var/www/html/admin/assets/afif/afif-pbx.css
check "login page shows AFIF-PBX" bash -c 'curl -s http://127.0.0.1/admin/config.php | grep -q "AFIF-PBX"'
check "no FreePBX in page title"  bash -c '! curl -s http://127.0.0.1/admin/config.php | grep -o "<title>[^<]*" | grep -qi freepbx'
check "usb_modeswitch installed"  command -v usb_modeswitch
check "afif-dongle helper"        /usr/local/bin/afif-dongle status
check "fwconsole reload"          fwconsole reload

echo "--- dongle devices ---"; asterisk -rx "dongle show devices" || true
echo "--- endpoints ---"; asterisk -rx "pjsip show endpoints" 2>/dev/null | tail -n 25 || true
exit $fail
