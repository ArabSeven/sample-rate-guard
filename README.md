# Sample Rate Guard

[![Build](https://github.com/ArabSeven/sample-rate-guard/actions/workflows/build.yml/badge.svg)](https://github.com/ArabSeven/sample-rate-guard/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**A tiny, silent Windows utility that notices when something changes your audio sample rate, tells you exactly what did it, and puts it back.**

You set your audio interface to 48 kHz. Days later you find everything running at 44.1 kHz: your interface, your virtual mixer, your DAW, your stream. Nothing tells you when it happened or why. Sample Rate Guard does.

- 🔔 **One notification per incident**, naming the culprit, e.g. *"Sample rate changed to 44.1 kHz. By: Sonar - MySong.cwp"*
- ↩️ **Restores your rate automatically**, but never fights your DAW while it's open (it restores right after you close it)
- 🪶 **Event-driven, no polling**: Windows wakes it only when an audio device changes. ~40 KB exe, no window, no tray icon, 0% CPU while idle
- 🎛️ **Works with or without a mixer**: it watches the devices themselves, so it doesn't matter whether you use Voicemeeter, a vendor mixer app, a DAW or nothing at all
- 🔒 **No network, no telemetry, no admin rights, no installer**. One exe, one settings file, one log

> [!IMPORTANT]
> **Testing status:** so far this has only been tested with a **Universal Audio Volt 1** on Windows 11, with Voicemeeter, Cantabile and Cakewalk Sonar in the chain. We did our best to make it work across devices and drivers: it uses only standard Windows audio interfaces plus one widely used USB-audio driver API, and degrades to "notify only" when it can't safely change a rate. Still, your hardware is the real test. **If something doesn't work, please [open an issue](https://github.com/ArabSeven/sample-rate-guard/issues/new/choose)** with your device, driver and the log, and we'll get to it.

---

## Contents

- [Why sample rates change behind your back](#why-sample-rates-change-behind-your-back)
- [How it works](#how-it-works)
- [Compatibility](#compatibility)
- [Install](#install)
- [Configuration](#configuration)
- [Command line](#command-line)
- [Notifications and Do Not Disturb](#notifications-and-do-not-disturb)
- [Reading the log](#reading-the-log)
- [Build from source](#build-from-source)
- [Footprint and privacy](#footprint-and-privacy)
- [Limitations](#limitations)
- [Contributing and issues](#contributing-and-issues)
- [License](#license)

---

## Why sample rates change behind your back

A device has one hardware clock. Anything that's allowed to set it can, and the last one wins. Common culprits:

| Cause | What happens |
|---|---|
| **A DAW opening a project at another rate** | Many DAWs set the interface to the *project's* rate over ASIO. An old 44.1 kHz project (or one created while the device happened to be at 44.1 kHz) silently switches everything. |
| **Apps using exclusive mode** | With "Allow applications to take exclusive control" enabled, a player, browser or game can open the device at its own rate. |
| **Driver / device re-enumeration** | Driver updates, USB power saving, sleep/resume or replugging can bring a device back at a default rate. |
| **Vendor control apps and helper services** | Companion apps that talk to the device can apply stored or default settings when they start. |
| **Windows defaults for new device entries** | Some drivers present a *new* Windows device entry per rate. Windows gives each new entry its own default format, so your earlier choice doesn't apply to it. |

Because virtual mixers, routing tools and DAWs usually follow the hardware clock, one change ripples through your whole chain.

## How it works

```
 Windows audio (IMMNotificationClient) ──event──▶ debounce ──▶ check rates
                                                                    │
                     ┌──────────────────────────────────────────────┤
                     ▼                                              ▼
         Windows device format                         Interface hardware rate
      (any device: shared-mode format)           (Thesycon-based USB audio drivers)
                     │                                              │
                     └───────────────┬──────────────────────────────┘
                                     ▼
              off target? ─▶ find culprit ─▶ notify once ─▶ restore (unless a DAW is open)
```

1. **No polling.** The guard registers for Windows audio endpoint notifications and sleeps. Device added/removed/state change, default device change or a change of a device's format wakes it. Events come in bursts, so it waits `DebounceMs` before checking.
2. **Two kinds of rate are watched:**
   - **Windows device format** of the devices you select (shared-mode "Default Format" in Sound settings). Works for any device.
   - **Interface hardware rate**, read directly from the driver for interfaces using a **Thesycon-based USB audio driver** (used by many USB audio interfaces, including the UA Volt series). These are detected automatically: the guard reads your ASIO driver registrations and looks for the driver's companion API DLL next to the ASIO DLL. No vendor-specific code or configuration is involved.
3. **Finding the culprit (best effort):**
   1. a DAW from your `DeferTo` list, with its window title (usually the open project)
   2. otherwise the newest program that has the **affected device's ASIO driver** loaded
   3. otherwise apps with an active audio session on the affected Windows device
   4. otherwise programs started in the last 2 minutes
4. **Notify once** per incident, until everything is back on target.
5. **Restore**, unless a `DeferTo` program (your DAW) is running. Then it waits for that program to exit and restores right after. Hardware rates are restored through the driver API; Windows formats through the same interface the Sound control panel uses. If something keeps changing the rate back (`MaxRestores` in `RestoreWindowSeconds`), the guard stops restoring rather than fighting.

## Compatibility

| Setup | Detect + notify | Restore | Notes |
|---|---|---|---|
| **UA Volt 1** (UA Volt driver, Thesycon-based) | ✅ tested | ✅ tested | Hardware rate via the driver API |
| Other interfaces with a **Thesycon-based** driver | ✅ expected | ✅ expected | Auto-detected if the ASIO DLL `…asio_x64.dll` has a sibling `…api_x64.dll` exporting the TUSBAUDIO API. Not yet tested, so reports welcome |
| Built-in audio, HDMI, USB class-compliant devices on Microsoft's driver, virtual devices (Voicemeeter, VB-Cable, …) | ✅ expected | ✅ expected | Via the Windows device format |
| Interfaces with other proprietary drivers | ✅ when Windows' device format changes | ⚠️ often not | Many drivers only expose the *current* hardware rate to Windows, so Windows can't switch it. The guard logs this and stays notify-only for that device. Use the vendor's control panel to set the rate |
| Bluetooth headsets | ⚠️ | ⚠️ | They often run at fixed rates. Don't include them in `Devices` |

**Mixers and routing software** (Voicemeeter, vendor mixer apps, streaming mixers, Cantabile-style hosts, DAWs): the guard doesn't integrate with any of them and doesn't need to. It watches the devices underneath. Your mixer keeps following the device clock as before, and the guard makes sure that clock stays where you want it.

**Windows:** Windows 10 and 11, 64-bit. Tested on Windows 11.

## Install

1. Download `SampleRateGuard.exe` from [Releases](https://github.com/ArabSeven/sample-rate-guard/releases) (or [build it yourself](#build-from-source)).
2. Put it in a folder you'll keep, e.g. `C:\Tools\SampleRateGuard\`.
3. Run once from that folder:
   ```bat
   SampleRateGuard.exe --install
   ```
   This makes it start at sign-in (per user, no admin), starts it now, and shows a confirmation notification. A `SampleRateGuard.ini` is created next to the exe on first run.
4. Edit `SampleRateGuard.ini` if needed (at least check `TargetRate` and `Devices`), then run `SampleRateGuard.exe --install` again to restart it.
5. Check what it sees:
   ```bat
   SampleRateGuard.exe --status
   ```

> **SmartScreen:** release builds aren't code-signed, so Windows may warn the first time. The source is a single file in this repo, and you can build it yourself with the C# compiler built into Windows.

**Uninstall:** `SampleRateGuard.exe --uninstall`, then delete the folder.

## Configuration

`SampleRateGuard.ini` lives next to the exe (or in `%LOCALAPPDATA%\SampleRateGuard\` if that folder isn't writable). Restart the guard after editing (`--install` again, or sign out and in).

| Setting | Default | Meaning |
|---|---|---|
| `TargetRate` | `48000` | The rate to keep, in Hz |
| `Devices` | `default` | Which Windows devices to watch: `default` (current default playback + recording), `all`, or comma-separated parts of device names, e.g. `Volt, Scarlett` or `default, Volt` |
| `Restore` | `true` | Put the rate back automatically. `false` = notify only |
| `Notify` | `true` | Show a notification once per incident |
| `DeferTo` | common DAWs | Process names (without `.exe`, `*` = prefix wildcard) that may run another rate. While one is running the guard only notifies and restores after it closes. Default: `Sonar, Cakewalk, reaper, Ableton Live*, FL64, FL, Cubase*, Nuendo*, Studio One, Bitwig Studio, ProTools, Reason, Waveform*, Mixcraft*, Ardour*` |
| `DriverApi` | `auto` | Interface hardware-rate access: `auto` (detect Thesycon-based drivers), `none`, or the full path to a vendor `…api_x64.dll` |
| `MaxRestores` / `RestoreWindowSeconds` | `3` / `120` | Anti-fighting limit |
| `DebounceMs` | `1500` | Wait for device-event bursts to settle |

Interface hardware found through `DriverApi` is always watched, whatever `Devices` says. Set `DriverApi=none` if you don't want that.

> **Tip:** If your DAW plays projects at other rates using its own real-time sample-rate conversion, you can remove it from `DeferTo` so the guard restores your rate even while it's open. Only do that if your DAW handles it gracefully.

## Command line

| Command | What it does |
|---|---|
| *(no arguments)* | Run the guard (silently; only one instance runs) |
| `--install` | Start at sign-in (HKCU Run), restart the guard, confirm with a notification |
| `--uninstall` | Remove from sign-in, stop it, remove its notification registration |
| `--stop` | Stop the running guard |
| `--status` | Log and show what's being watched and the current rates |
| `--test-notify` | Show a sample notification |

## Notifications and Do Not Disturb

Notifications appear as **Sample Rate Guard** and are sent as Windows *important* notifications. They show up in Notification Center even when Do Not Disturb is on. To have them **pop up** during Do Not Disturb:

1. Run `SampleRateGuard.exe --test-notify` once, so Windows knows the app.
2. Settings → System → Notifications → **Set priority notifications** → **Add apps** → **Sample Rate Guard**.
3. If Windows asks whether to allow important notifications from Sample Rate Guard, accept.

## Reading the log

`SampleRateGuard.log` only gets a line when something happens:

```
2026-11-11 17:57:28  Sample Rate Guard started. target 48000 Hz, devices 'default,Volt', restore=True, notify=True
    watching: Universal Audio Volt (interface hardware) = 48000 Hz
    watching: MONITOR L/R (Volt 1) [playback] = 48000 Hz
    watching: INPUT (Volt 1) [recording] = 48000 Hz
    ASIO drivers: Universal Audio Volt, Voicemeeter Virtual ASIO
2026-11-11 17:59:54  RATE CHANGED (device change): Universal Audio Volt (interface hardware): 44100 Hz. Caused by: reaper. reaper is open, so it's left alone; 48000 Hz will be restored when it closes. | ASIO clients: … | started in the last 2 min: reaper
2026-11-11 17:59:54  notification shown: Sample rate changed to 44.1 kHz
2026-11-11 18:00:05  reaper closed.
2026-11-11 18:00:08  Restore Universal Audio Volt (interface hardware) -> 48000 Hz: ok
2026-11-11 18:00:10  All watched devices back at 48000 Hz.
```

Everything after `|` is supporting evidence: all ASIO hosts, apps using the device, recently started programs. The log rotates at 2 MB, keeping one `.old` file.

## Build from source

No Visual Studio, SDK or NuGet needed. Windows already includes the C# compiler (.NET Framework 4.x).

```bat
git clone https://github.com/ArabSeven/sample-rate-guard.git
cd sample-rate-guard
build.cmd
```

Output: `bin\SampleRateGuard.exe`.

**Requirements:** Windows 10/11 x64 with .NET Framework 4.8 (built in). If `csc.exe` isn't found, enable *.NET Framework 4.8 Advanced Services* in "Turn Windows features on or off".

**What `build.cmd` does:** compiles `src\SampleRateGuard.cs` as a 64-bit windowless exe with `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, referencing the Windows Runtime metadata in `%WINDIR%\System32\WinMetadata` for notifications. The code is C# 5 (that compiler's language level), in a single file.

**CI:** every push is built on GitHub Actions (`windows-latest`). Tags `v*` publish a release with the exe and its SHA-256.

## Footprint and privacy

| | |
|---|---|
| Files | `SampleRateGuard.exe` (~40 KB), `SampleRateGuard.ini`, `SampleRateGuard.log` (+ `.old`) |
| Registry (HKCU only) | `Software\Microsoft\Windows\CurrentVersion\Run\Sample Rate Guard` (with `--install`), `Software\Classes\AppUserModelId\SampleRateGuard` (notification name) |
| Resources | ~20–45 MB RAM, 0% CPU while waiting; a second or two of CPU when an incident happens (scanning which programs have an ASIO driver loaded) |
| Privileges | Runs as your user. No admin, no service, no driver |
| Network | None. No telemetry, no update checks |

## Limitations

- **It can't make a rate change impossible.** The ASIO standard lets any host set any rate the hardware supports, and Windows has no lock. The guard makes unwanted rates short-lived and visible.
- **Culprit detection is best effort.** Windows doesn't record who changed a rate. The guard reports the most likely program plus the evidence. If a tool calls a driver API directly, it can only show "recently started" programs.
- **The Windows-format restore uses `IPolicyConfig`**, the undocumented interface behind the Sound control panel. It has been stable across Windows 10 and 11 but isn't an official API.
- **Interfaces that only expose their current rate to Windows** can't be switched via Windows. For Thesycon-based drivers the guard uses the driver API instead. For others, it notifies and you switch it in the vendor's control panel.
- **Only tested on a UA Volt 1** (see the note at the top).

## Contributing and issues

Reports from other hardware are the most valuable contribution right now.

- **Something not detected, wrong culprit, restore failing?** [Open an issue](https://github.com/ArabSeven/sample-rate-guard/issues/new/choose) with your Windows version, interface and driver version, mixer/DAW, the output of `SampleRateGuard.exe --status` (in the log) and the relevant log lines.
- **It works on your interface?** Please tell us too (there's an issue template for that), so we can extend the compatibility table.
- **Pull requests welcome.** Keep it a single, dependency-free C# 5 file that builds with `build.cmd`.

## License

[MIT](LICENSE). Free to use, modify and share.
