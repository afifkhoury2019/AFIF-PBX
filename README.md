# AFIF-PBX

AFIF-PBX is a business phone system for **Windows 11** with a **Huawei E303 GSM gateway**, by AFIF-TECH.
It is a clone of the Raspberry Pi FreePBX system (`RPBX.img`), rebuilt to run on a Windows PC: same extensions, trunks, routes, voicemail and dongle setup, under the AFIF-PBX name and logo.

## How it works

| Part | What it is |
|---|---|
| **AFIF-PBX system** | Debian 12 running inside WSL 2 with Asterisk 22, the FreePBX 17 open-source engine and `chan_dongle` for the E303. Your Raspberry Pi configuration is restored into it during the build. |
| **AFIF-PBX launcher** (`AFIF-PBX.exe`) | Tray app with an animated logo splash. It starts the PBX at sign-in, passes the E303 through to it automatically (usbipd-win) and shows engine / dongle status. |
| **Web interface** | `http://localhost/` (or the PC's LAN IP from phones): animated AFIF-PBX landing page and the rebranded admin panel. |
| **Networking** | WSL mirrored mode — phones register to the Windows PC's own IP address. |

## Building

Everything builds in GitHub Actions (`.github/workflows/build.yml`):

1. **Read the Raspberry Pi image** — downloads `RPBX.img` from Google Drive, mounts it read-only, dumps the database and copies the Asterisk/dongle configuration. The step summary shows a password-free inventory; `CREDENTIALS.md` is in the private `credentials` artifact.
2. **Branding assets** — logo, icon and installer artwork from `branding/`.
3. **AFIF-PBX system** — builds the WSL root filesystem, compiles `chan_dongle`, restores the Pi configuration, applies branding and runs self-tests.
4. **Windows** — builds the launcher and the `AFIF-PBX-Setup-x.y.z.exe` installer (Inno Setup) and publishes it as a release.

Run it from **Actions → Build AFIF-PBX → Run workflow**. Leave the Drive id empty for a clean system without the restore.

> Keep this repository **private** — the build reads your PBX passwords.

## Your logo

Put the official logo at `branding/logo.png` (transparent PNG, wide) and optionally a square `branding/icon.png`, then run the workflow again. Until then a placeholder AFIF-PBX mark (`branding/logo.svg`) is used.

## Installing on Windows 11

1. Run `AFIF-PBX-Setup-….exe` as administrator. It installs/updates WSL, usbipd-win and the AFIF-PBX system, opens the SIP/RTP/web ports and adds AFIF-PBX to sign-in.
   If WSL was not installed yet, Windows restarts once and setup continues by itself.
2. AFIF-PBX starts with its splash screen. Log in at `http://localhost/` with the **same admin username and password as on the Raspberry Pi**.
3. Plug in the E303 with your SIM (PIN disabled). The launcher shares it with the PBX, Linux switches it from CD-ROM to modem mode, and `chan_dongle` picks it up. The first time, Windows may need you to unplug and re-plug it once.
4. Point your phones at the PC's IP address (shown in the control panel).

## Useful commands

From the launcher: **Asterisk CLI**, **Rescan dongle**, **Reload**, **Restart**. Inside the system (`wsl -d AFIF-PBX`):

```bash
afif-dongle status     # USB / serial ports / chan_dongle view
afif-rebrand           # re-apply AFIF-PBX branding after FreePBX module updates
asterisk -rx "dongle show devices"
```

## Licences

AFIF-PBX is built on open-source software: Asterisk (GPLv2), FreePBX open-source modules (GPLv3+/AGPLv3), chan_dongle (GPLv2), Debian, usbipd-win (GPLv3). FreePBX® is a trademark of Sangoma Technologies; AFIF-PBX is not affiliated with Sangoma. Commercial FreePBX modules are not included. Source for all GPL components is available from their upstream projects linked in the build scripts.
