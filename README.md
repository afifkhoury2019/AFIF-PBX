# AFIF-PBX

AFIF-PBX is the AFIF-TECH phone system for **Windows**. It runs as a normal Windows service, with **no WSL, Linux or virtual machine**. It uses a **Huawei GSM dongle** (E303, E1550, E173…) as a phone line.

## Features

| Feature | How it works |
|---|---|
| **Extensions** | Built-in SIP server. IP phones and softphones (Zoiper, MicroSIP, Grandstream, Yealink…) register with the extension number and its secret. |
| **GSM line in and out** | Direct driver for Huawei voice dongles through the two COM ports the Huawei Windows driver creates: *PC UI Interface* for commands and *Application Interface* for voice. Calls to the SIM go to any destination. Extensions dial outside numbers through the SIM. |
| **Ring groups** | Ring all members at once, or one after another, with a fallback destination. |
| **IVR menus** | Greeting spoken by Windows text-to-speech (including Arabic voices) or an uploaded recording. Keys 0–9 route the caller, and callers can dial extensions directly. |
| **Voicemail** | One mailbox per extension. Messages can be played in the web panel or by dialling **\*97** from a phone. |
| **SMS** | Send and receive through the SIM, including Arabic (Unicode) and long multi-part messages. |
| **Call recording** | Per extension, or every call on the GSM line. Recordings are stereo WAV files that can be played and downloaded in the panel. |
| **Web panel** | `http://<pc>/`, branded AFIF-PBX with the animated logo render. Includes dashboard, live calls, call log, GSM status and signal, USSD balance check, prompts, backup and logs. |
| **Tray app** | Animated splash screen, status window, and alerts when the dongle changes state or new voicemail arrives. |

Feature codes: **\*97** voicemail · **\*43** echo test · **\*&lt;ext&gt;** leave a voicemail for an extension.

## Install

1. Download `AFIF-PBX-Setup-x.y.z.exe` from **Releases** and run it as administrator.
   If the old WSL edition is installed, it is removed and the dongle is given back to Windows.
2. Install the **Huawei driver** if Windows does not show the dongle's COM ports. Open the dongle's virtual CD drive and run its setup (Mobile Partner / Mobile Connect). Then **exit Mobile Partner** and stop it from starting with Windows, because it holds the ports.
3. Open **http://localhost/**. Sign in with `admin` / `admin` and choose a new password.
4. Under **Extensions**, note each extension's secret. On each phone, set the server to the PC's IP address, the user name to the extension number, and the password to the secret.
5. Under **GSM line**, choose where calls to the SIM go. Use a SIM with the **PIN turned off**.

## Remote phones — the built-in secure network

AFIF-PBX carries its own Tailscale engine (`afif-net.exe`, built from `native/afif-net` with Tailscale's `tsnet`).
It is a userspace node: **no VPN, network adapter or route is added to Windows**, and only the phone system is on your
tailnet. Nothing else needs to be installed on the PC, and no router ports are opened.

1. Panel → **Secure network** → **Switch on and connect** → sign in with your Tailscale account (or paste an auth key).
   In the Tailscale admin console, open *afif-pbx* → **Disable key expiry**.
2. **Your domain (optional):** enter `voip.powerchain.uk` → **Point the name to this PBX** (DNS-only record at the PBX's 100.x address; needs a Cloudflare DNS token with Zone · DNS · Edit).
3. **Mobile:** **AFIF Phone** (`/mobile`) has the same engine built in and uses it **only for its own traffic**. On first start choose **Connect**, sign in with the same account,
   then add the extension: domain `voip.powerchain.uk:5160`, transport **UDP**.
4. Dial **\*43** for an echo test.

How it works: afif-net answers on the PBX's tailnet address for SIP (5060, 5160) and voice (UDP 21000–21199) and relays
each phone's packets to AFIF-PBX on 127.0.0.1; the PBX advertises its tailnet address in SIP and SDP for those phones.
The Windows Firewall rule for the PBX itself allows the local network only.

## Building

Everything builds on GitHub Actions (`.github/workflows/native.yml`):

- **Build and test the PBX core** runs on Linux. It tests real SIP calls between software phones, ring groups, IVR key routing, voicemail, call recording, and a simulated Huawei E303 for GSM calls in and out and for SMS in Arabic.
- **Windows** builds the service and tray app, starts the service and checks the web panel and API, then builds the Inno Setup installer.

Run **Actions → AFIF-PBX Native → Run workflow** with *publish release* ticked to publish an installer.

`native/src/AfifPbx.Core` contains the PBX: SIP (SIPSorcery), media, the GSM driver, call logic, and storage. `native/src/AfifPbx.Server` is the Windows service and web panel. `native/src/AfifPbx.Tray` is the tray app.

## Your logo

Put the official logo at `branding/logo.png` (wide, transparent background), and optionally a square `branding/icon.png`. The next build uses them everywhere.

## Licences

AFIF-PBX uses SIPSorcery (BSD-3-Clause), Microsoft .NET (MIT) and SQLite (public domain). The earlier WSL edition (`rootfs/`, `windows/`, `.github/workflows/build.yml`) is kept for reference and is no longer built automatically.
