"""AFIF-PBX image inventory.

Reads the bundle produced by extract.sh and writes:
  <out>/INVENTORY.md         -- safe to show in build logs (no passwords, IMEI masked)
  <bundle>/CREDENTIALS.md    -- every credential found; stays inside the private artifact/release
"""
import configparser
import csv
import os
import re
import sys

bundle, out = sys.argv[1], sys.argv[2]
meta = os.path.join(bundle, "meta")
fs = os.path.join(bundle, "fs")


def read(path, default=""):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read().strip()
    except OSError:
        return default


def tsv(name):
    p = os.path.join(meta, "q", name + ".tsv")
    if not os.path.exists(p) or os.path.getsize(p) == 0:
        return []
    with open(p, encoding="utf-8", errors="replace") as f:
        return list(csv.DictReader(f, delimiter="\t", quoting=csv.QUOTE_NONE))


def ini(path):
    """Parse an Asterisk-style .conf loosely (duplicate keys allowed, last wins)."""
    cp = configparser.RawConfigParser(strict=False, allow_no_value=True,
                                      delimiters=("=",), comment_prefixes=(";",),
                                      inline_comment_prefixes=(";",))
    cp.optionxform = str
    txt = read(path)
    if not txt:
        return cp
    txt = re.sub(r"^\s*#(include|exec|tryinclude).*$", "", txt, flags=re.M)
    txt = re.sub(r"^\s*\[([^\]]+)\]\(.*\)", r"[\1]", txt, flags=re.M)  # templates
    try:
        cp.read_string("[__top__]\n" + txt)
    except configparser.Error:
        pass
    return cp


def mask(s, keep=4):
    s = s or ""
    return s if len(s) <= keep else "•" * (len(s) - keep) + s[-keep:]


def table(rows, cols):
    if not rows:
        return "_none_\n"
    o = "| " + " | ".join(cols) + " |\n|" + "---|" * len(cols) + "\n"
    for r in rows:
        o += "| " + " | ".join((r.get(c) or "").replace("|", "\\|") for c in cols) + " |\n"
    return o


osr = dict(l.split("=", 1) for l in read(os.path.join(meta, "os-release")).splitlines() if "=" in l)
facts = {
    "Hostname": read(os.path.join(meta, "hostname")),
    "OS": osr.get("PRETTY_NAME", "").strip('"'),
    "CPU": read(os.path.join(meta, "arch")),
    "Asterisk": read(os.path.join(meta, "asterisk-version")),
    "FreePBX framework": read(os.path.join(meta, "freepbx-version")),
    "Database server": read(os.path.join(meta, "db-server")),
    "chan_dongle module": read(os.path.join(meta, "chan_dongle-path")) or "not found",
    "Call recordings (not copied)": read(os.path.join(meta, "monitor-size")),
}

# ---- dongle.conf ----
dongle = ini(os.path.join(fs, "etc/asterisk/dongle.conf"))
dongles = []
for sec in dongle.sections():
    if sec in ("__top__", "general", "defaults"):
        continue
    d = dict(dongle.items(sec))
    dongles.append({"name": sec,
                    "imei": d.get("imei", ""), "imsi": d.get("imsi", ""),
                    "audio": d.get("audio", ""), "data": d.get("data", ""),
                    "context": d.get("context") or dongle.get("defaults", "context", fallback=""),
                    "exten": d.get("exten", "")})

# ---- freepbx.conf / amportal.conf ----
fpbx = read(os.path.join(fs, "etc/freepbx.conf"))
db = dict(re.findall(r"\$amp_conf\[['\"](AMPDB\w+)['\"]\]\s*=\s*['\"]([^'\"]*)['\"]", fpbx))
amportal = read(os.path.join(fs, "etc/amportal.conf"))
for k, v in re.findall(r"^(AMP\w+)=(.*)$", amportal, flags=re.M):
    db.setdefault(k, v)

manager = ini(os.path.join(fs, "etc/asterisk/manager.conf"))
ami = [(s, manager.get(s, "secret", fallback="")) for s in manager.sections()
       if s not in ("__top__", "general") and manager.has_option(s, "secret")]

users, devices = tsv("users"), tsv("devices")
sip, pjsip, iax = tsv("sip"), tsv("pjsip"), tsv("iax")
trunks = tsv("trunks")
secrets = {}
for r in sip + pjsip + iax:
    secrets.setdefault(r["id"], {})[r["keyword"]] = r["data"]

# ---------------- INVENTORY (no secrets) ----------------
inv = ["# AFIF-PBX — source image inventory\n",
       "Passwords are deliberately left out of this file. They are in `CREDENTIALS.md` inside the private bundle.\n",
       "## System\n", table([{"Item": k, "Value": v} for k, v in facts.items()], ["Item", "Value"]),
       "\n## GSM dongles (chan_dongle)\n",
       table([{**d, "imei": mask(d["imei"]), "imsi": mask(d["imsi"])} for d in dongles],
             ["name", "imei", "imsi", "audio", "data", "context", "exten"]),
       f"\n## Extensions ({len(users)})\n", table(users, ["extension", "name", "voicemail"]),
       f"\n## Trunks ({len(trunks)})\n", table(trunks, ["trunkid", "name", "tech", "channelid", "disabled"]),
       "\n## Inbound routes\n", table(tsv("incoming"), ["did", "cidnum", "destination", "description"]),
       "\n## Outbound routes\n", table(tsv("outbound_routes"), ["route_id", "name", "trunks"]),
       "\n## Ring groups\n", table(tsv("ringgroups"), ["grpnum", "description", "grplist", "strategy"]),
       "\n## IVRs\n", table(tsv("ivr"), ["id", "name", "description"]),
       "\n## Queues\n", table(tsv("queues"), ["extension", "descr"]),
       "\n## Time conditions\n", table(tsv("timeconditions"), ["timeconditions_id", "displayname"]),
       "\n## Modules\n", table(tsv("modules"), ["modulename", "version", "enabled"]),
       "\n## Files copied\n```\n" + read(os.path.join(meta, "copied.txt")) + "\n```\n"]
inv_txt = "".join(inv)
with open(os.path.join(out, "INVENTORY.md"), "w", encoding="utf-8") as f:
    f.write(inv_txt)

# ---------------- CREDENTIALS (private) ----------------
cred = ["# AFIF-PBX — credentials recovered from the source image\n",
        "**Private.** Keep this file out of public places.\n",
        "\n## Web admin (FreePBX `ampusers`)\n",
        "FreePBX stores only a SHA-1 hash of the admin password, so the clear-text password cannot be read back. "
        "The same login keeps working after the restore because the hash is restored with it.\n\n",
        table(tsv("ampusers"), ["username", "password_sha1", "sections"]),
        "\n## Database\n",
        table([{"Key": k, "Value": v} for k, v in sorted(db.items()) if k.startswith("AMPDB") or k in ("AMPMGRUSER", "AMPMGRPASS")],
              ["Key", "Value"]),
        "\n## Asterisk Manager (AMI)\n", table([{"user": u, "secret": s} for u, s in ami], ["user", "secret"]),
        "\n## Extensions\n",
        table([{"extension": u["extension"], "name": u["name"],
                "tech": next((d["tech"] for d in devices if d["id"] == u["extension"]), ""),
                "secret": secrets.get(u["extension"], {}).get("secret", "")} for u in users],
              ["extension", "name", "tech", "secret"]),
        "\n## Trunks\n"]
for t in trunks:
    keys = secrets.get(t.get("channelid", ""), {}) or secrets.get("tr-peer-" + t["trunkid"], {}) \
        or secrets.get(t["trunkid"], {})
    cred.append(f"\n**{t['name']}** ({t['tech']})\n\n")
    cred.append(table([{"keyword": k, "value": v} for k, v in keys.items()], ["keyword", "value"]))
cred += ["\n## GSM dongles\n", table(dongles, ["name", "imei", "imsi", "audio", "data", "context", "exten"]),
         "\n## Linux login\n",
         "Linux account passwords are stored as one-way hashes and cannot be recovered. "
         "On Windows the AFIF-PBX system is opened from the launcher (Asterisk CLI / shell) without a Linux password.\n"]
smtp = read(os.path.join(fs, "etc/postfix/sasl_passwd")) or read(os.path.join(fs, "etc/msmtprc"))
if smtp:
    cred += ["\n## Outgoing mail relay\n```\n", smtp, "\n```\n"]
with open(os.path.join(bundle, "CREDENTIALS.md"), "w", encoding="utf-8") as f:
    f.write("".join(cred))

print(inv_txt)
