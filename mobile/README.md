# AFIF Phone (Android)

SIP softphone for AFIF-PBX with a built-in secure network, so extensions work away from home.

* Base: [Linphone for Android](https://github.com/BelledonneCommunications/linphone-android) — GPLv3. AFIF Phone is therefore GPLv3 too; this folder is its corresponding source.
* Secure network engine and screens: [tailscale-android](https://github.com/tailscale/tailscale-android) — BSD-3-Clause.
* Exact upstream commits: `pins.env`.

## How it is built
`.github/workflows/mobile.yml` fetches both projects, builds the Tailscale engine (`libtailscale.aar`, Go + NDK),
runs `apply.py` (name, package `com.afiftech.phone`, colours, icons, splash, "Secure network" menu entry,
AFIF-PBX presets), builds the release APK, starts it on an emulator and publishes a `phone-v*` release.

* `make_res.py` regenerates the launcher icons and splash wordmark in `res/` (replace the mark there with the official logo).
* `overlay/` holds the vector logos and extra strings copied over the Linphone resources.

## Signing
Without secrets every build is signed with a throw-away key (you must uninstall before installing a newer build).
For a permanent key add the repository secrets `AFIF_PHONE_KEYSTORE` (base64 of a .jks with alias `afif-phone`)
and `AFIF_PHONE_KEY_PASSWORD`.
