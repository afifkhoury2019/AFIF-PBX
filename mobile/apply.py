#!/usr/bin/env python3
"""
AFIF Phone build overlay.

Turns a checkout of linphone-android into "AFIF Phone":
  * AFIF branding (name, package, colours, icons, splash, logos)
  * the Tailscale engine + its sign-in/status screens as the :tailscale module ("Secure network")
  * AFIF-PBX presets (domain voip.powerchain.uk:5160, UDP, keep-alive, no Linphone cloud services)

Usage: apply.py --linphone DIR --tailscale DIR   (version: gradle -PafifVersionName=X -PafifVersionCode=N)
Every edit is checked: if an upstream file changed shape, the script stops with a clear message.
"""
import argparse
import os
import re
import shutil
import sys

ap = argparse.ArgumentParser()
ap.add_argument("--linphone", required=True)
ap.add_argument("--tailscale", required=True)
ap.add_argument("--res", default=None, help="branding PNGs (default: mobile/res, made by make_res.py)")
args = ap.parse_args()

HERE = os.path.dirname(os.path.abspath(__file__))
args.res = args.res or os.path.join(HERE, "res")
pins = dict(l.strip().split("=", 1) for l in open(os.path.join(HERE, "pins.env")) if "=" in l and not l.startswith("#"))
APP_ID, APP_NAME = pins["APP_ID"], pins["APP_NAME"]
L = os.path.abspath(args.linphone)
T = os.path.abspath(args.tailscale)
APP = os.path.join(L, "app")
MAIN = os.path.join(APP, "src", "main")


def read(p):
    with open(p, encoding="utf-8") as f:
        return f.read()


def write(p, s):
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(s)


def edit(path, old, new, count=1, regex=False):
    s = read(path)
    if regex:
        s2, n = re.subn(old, new, s, count=0 if count == 0 else count, flags=re.S)
    else:
        n = s.count(old)
        s2 = s.replace(old, new) if count == 0 else s.replace(old, new, count)
    if n == 0:
        sys.exit(f"apply.py: pattern not found in {os.path.relpath(path, L)}:\n  {old[:160]!r}")
    write(path, s2)
    print(f"  edited {os.path.relpath(path, L) if path.startswith(L) else path} ({min(n, count) if count else n}x)")


print("== Gradle project")
kotlin_ver = re.search(r'^kotlin\s*=\s*"([^"]+)"', read(os.path.join(L, "gradle", "libs.versions.toml")), re.M).group(1)
print("  Kotlin", kotlin_ver)
edit(os.path.join(L, "settings.gradle.kts"), 'include(":app")', 'include(":app")\ninclude(":tailscale")')
edit(os.path.join(L, "settings.gradle.kts"), 'rootProject.name = "Linphone"', 'rootProject.name = "AFIF-Phone"')
edit(os.path.join(L, "settings.gradle.kts"),
     "    repositories {\n        google()\n        mavenCentral()\n\n        maven {",
     "    versionCatalogs {\n        create(\"tslibs\") { from(files(\"tailscale/libs.versions.toml\")) }\n    }\n\n"
     "    repositories {\n        google()\n        mavenCentral()\n        // libtailscale.aar built by the CI\n"
     "        maven { url = uri(File(rootDir, \"localrepo\")) }\n\n        maven {")
edit(os.path.join(L, "build.gradle.kts"), "    alias(libs.plugins.crashlytics) apply false",
     "    alias(libs.plugins.crashlytics) apply false\n"
     f'    id("org.jetbrains.kotlin.plugin.compose") version "{kotlin_ver}" apply false\n'
     f'    id("org.jetbrains.kotlin.plugin.serialization") version "{kotlin_ver}" apply false')

print("== :tailscale module (from tailscale-android)")
TS = os.path.join(L, "tailscale")
shutil.rmtree(TS, ignore_errors=True)
shutil.copytree(os.path.join(T, "android", "src", "main"), os.path.join(TS, "src", "main"))
shutil.copy(os.path.join(T, "android", "proguard-rules.pro"), os.path.join(TS, "proguard-rules.pro"))
shutil.copy(os.path.join(T, "android", "gradle", "libs.versions.toml"), os.path.join(TS, "libs.versions.toml"))
shutil.copy(os.path.join(HERE, "tailscale-module.gradle.kts"), os.path.join(TS, "build.gradle.kts"))
shutil.copy(os.path.join(HERE, "afif-proguard.pro"), os.path.join(TS, "afif-proguard.pro"))
shutil.copy(os.path.join(T, "LICENSE"), os.path.join(TS, "LICENSE-tailscale"))
TSJ = os.path.join(TS, "src", "main", "java", "com", "tailscale", "ipn")
# AFIF Phone's Application class extends Tailscale's
edit(os.path.join(TSJ, "App.kt"), "\nclass App : UninitializedApp()", "\nopen class App : UninitializedApp()")
man = os.path.join(TS, "src", "main", "AndroidManifest.xml")
# the app (AFIF Phone) owns <application> attributes; keep only Tailscale's components
edit(man, r"<application\b[^>]*>", "<application>", regex=True)
edit(man, '            <intent-filter>\n                <action android:name="android.intent.action.MAIN" />\n\n'
          '                <category android:name="android.intent.category.LAUNCHER" />\n'
          '                <category android:name="android.intent.category.LEANBACK_LAUNCHER" />\n            </intent-filter>\n', "")
edit(man, r'\s*<activity\s+android:name="ShareActivity".*?</activity>', "", regex=True)
edit(man, '            android:name="MainActivity"', '            android:name="MainActivity"\n'
          '            android:theme="@style/Theme.App.SplashScreen"')
edit(man, '            android:label="@string/app_name"\n            android:launchMode="singleTask"\n            android:windowSoftInputMode="adjustResize">',
     '            android:label="Secure network"\n            android:launchMode="singleTask"\n            android:windowSoftInputMode="adjustResize">')

print("== app module: identity, signing, dependency on :tailscale")
gb = os.path.join(APP, "build.gradle.kts")
edit(gb, 'val packageName = "org.linphone"', f'val packageName = "{APP_ID}"')
edit(gb, r'versionCode = \d+[^\n]*', 'versionCode = providers.gradleProperty("afifVersionCode").getOrElse("1").toInt()', regex=True)
edit(gb, 'println("Computed git version: $gitVersion")',
     'gitVersion = providers.gradleProperty("afifVersionName").getOrElse("1.0")\nprintln("Computed git version: $gitVersion")')
edit(gb, 'versionName = "6.3.0-alpha"', 'versionName = providers.gradleProperty("afifVersionName").getOrElse("1.0")')
edit(gb, 'output.outputFileName = "linphone-android-${variant.buildType.name}-$gitVersion.apk"',
     'output.outputFileName = "AFIF-Phone-${variant.buildType.name}.apk"')
edit(gb, 'abiFilters += listOf("armeabi-v7a", "arm64-v8a")',
     'abiFilters += providers.gradleProperty("afifAbis").getOrElse("armeabi-v7a,arm64-v8a").split(",")')
edit(gb, "dependencies {", 'dependencies {\n    implementation(project(":tailscale"))', count=1)
gs = os.path.join(APP, "google-services.json")
if os.path.exists(gs):
    os.remove(gs)  # Linphone's Firebase project only works for the official app
    print("  removed google-services.json (no Linphone push/crash reporting)")

print("== Application class")
edit(os.path.join(MAIN, "java", "org", "linphone", "LinphoneApplication.kt"),
     "class LinphoneApplication : Application(), SingletonImageLoader.Factory",
     "class LinphoneApplication : com.tailscale.ipn.App(), SingletonImageLoader.Factory")

print("== Name, colours")
n = 0
for d in sorted(os.listdir(os.path.join(MAIN, "res"))):
    p = os.path.join(MAIN, "res", d, "strings.xml")
    if d.startswith("values") and os.path.exists(p):
        s = read(p)
        s2 = re.sub(r'<!ENTITY appName "[^"]*">', f'<!ENTITY appName "{APP_NAME}">', s)
        s2 = s2.replace(">Linphone<", f">{APP_NAME}<")
        if s2 != s:
            write(p, s2)
            n += 1
print(f"  app name set in {n} language files")
colors = os.path.join(MAIN, "res", "values", "colors.xml")
edit(colors, '<color name="orange_main_100">#FFEACB</color>', '<color name="orange_main_100">#DCE8FF</color>')
edit(colors, '<color name="orange_main_500">#FF5E00</color>', '<color name="orange_main_500">#2563EB</color>')

print("== Icons, splash, logos")
for root, _, files in os.walk(args.res):
    for f in files:
        src = os.path.join(root, f)
        dst = os.path.join(MAIN, "res", os.path.relpath(src, args.res))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy(src, dst)
for root, _, files in os.walk(os.path.join(HERE, "overlay")):
    for f in files:
        src = os.path.join(root, f)
        dst = os.path.join(MAIN, os.path.relpath(src, os.path.join(HERE, "overlay")))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy(src, dst)
        print("  overlay", os.path.relpath(dst, L))
for icon in ("ic_launcher.xml", "ic_launcher_round.xml"):
    edit(os.path.join(MAIN, "res", "mipmap-anydpi", icon), '@color/orange_main_500', '@color/afif_launcher_background')

print("== Presets for AFIF-PBX")
rc = os.path.join(MAIN, "assets", "linphonerc_default")
edit(rc, 'contact="Linphone Android" <sip:linphone.android@unknown-host>', f'contact="{APP_NAME}" <sip:afif.phone@unknown-host>')
edit(rc, "[app]\n", "[app]\nkeep_service_alive=1\n")
# AFIF-PBX speaks plain RTP: never insist on SRTP (the phone would hang up the moment a call is answered)
edit(rc, "media_encryption=none\n", "media_encryption=none\nmedia_encryption_mandatory=0\n")
edit(rc, "version_check_url_root=https://download.linphone.org/releases\n", "version_check_url_root=\n")
edit(rc, "[ui]\ncontacts_filter=sip.linphone.org\n",
     "[ui]\ncontacts_filter=\n"
     "assistant_third_party_sip_account_domain=voip.powerchain.uk:5160\n"
     "assistant_third_party_sip_account_transport=udp\n"
     "assistant_hide_create_account=1\n"
     "disable_meetings_feature=1\n"
     "disable_chat_feature=1\n")
tp = os.path.join(MAIN, "assets", "assistant_third_party_default_values")
edit(tp, '<entry name="stun_server" overwrite="true">stun.linphone.org</entry>', '<entry name="stun_server" overwrite="true"></entry>')
edit(tp, '<entry name="protocols" overwrite="true">stun,ice</entry>', '<entry name="protocols" overwrite="true"></entry>')
edit(tp, '<entry name="media_encryption">srtp</entry>', '<entry name="media_encryption">none</entry>')

print("== 'Secure network' in the side menu")
drawer = os.path.join(MAIN, "res", "layout", "drawer_menu.xml")
edit(drawer, 'app:layout_constraintBottom_toTopOf="@id/settings" />',
     'app:layout_constraintBottom_toTopOf="@id/secure_network" />\n\n'
     '            <androidx.appcompat.widget.AppCompatTextView\n'
     '                style="@style/header_style"\n'
     '                android:id="@+id/secure_network"\n'
     '                android:layout_width="0dp"\n'
     '                android:layout_height="wrap_content"\n'
     '                android:layout_marginBottom="10dp"\n'
     '                android:layout_marginStart="10dp"\n'
     '                android:layout_marginEnd="10dp"\n'
     '                android:padding="5dp"\n'
     '                android:text="@string/afif_secure_network"\n'
     '                android:drawableStart="@drawable/lock_simple"\n'
     '                android:drawableEnd="@drawable/caret_right"\n'
     '                android:drawablePadding="8dp"\n'
     '                android:background="@drawable/squircle_transparent_button_background"\n'
     '                app:drawableTint="?attr/color_main2_500"\n'
     '                app:layout_constraintStart_toStartOf="parent"\n'
     '                app:layout_constraintEnd_toEndOf="parent"\n'
     '                app:layout_constraintBottom_toTopOf="@id/settings" />')
frag = os.path.join(MAIN, "java", "org", "linphone", "ui", "main", "fragment", "DrawerMenuFragment.kt")
edit(frag, "        binding.setHelpClickedListener {",
     "        binding.secureNetwork.setOnClickListener {\n"
     "            startActivity(Intent(requireContext(), com.tailscale.ipn.MainActivity::class.java))\n"
     "            (requireActivity() as MainActivity).closeDrawerMenu()\n"
     "        }\n\n"
     "        binding.setHelpClickedListener {")

print("AFIF Phone overlay applied.")
