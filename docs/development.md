# Development Guide

## Requirements

- **Windows**: Windows 10 1809+ / 11, Visual Studio 2022+ with the "Windows
  App SDK (WinUI 3)" and ".NET desktop development" workloads, .NET 8 SDK.
  Running the app unpackaged needs the Windows App SDK 1.5 runtime installed.
- **Android**: Android Studio (latest), JDK 17, Android SDK 34+. The app runs
  on Android 8.0 (API 26) and later.

## Getting started

```
git clone <repo>
cd bridge

# Windows (pick a platform; WinUI can't build as "Any CPU")
cd windows
dotnet restore Bridge.sln
dotnet build Bridge.sln -p:Platform=x64
dotnet test Bridge.Tests/Bridge.Tests.csproj
# or open Bridge.sln in Visual Studio, choose x64, and run Bridge.Windows

# Android
cd android
./gradlew assembleDebug
./gradlew testDebugUnitTest
# or open android/ in Android Studio and run the `app` configuration
```

`Bridge.Windows` runs unpackaged by default (F5 in Visual Studio).

## Packaging (MSIX)

```
# Windows, PowerShell, from the repo root (needs Visual Studio 2022):
.\windows\scripts\package-msix.ps1 -CreateDevCert          # x64, self-signed "CN=Bridge Dev"
.\windows\scripts\package-msix.ps1 -Platform arm64 -CertThumbprint <thumbprint>
```

The script builds a sideload MSIX into `windows/AppPackages/`, signs it, and
prints how to trust the dev certificate on a test PC (once, as admin) and
install it. The manifest (`Bridge.Windows/Package.appxmanifest`) declares:
toast activation (so reply/buttons work when Bridge was closed), the
`BridgeStartup` startup task (Settings › "Start Bridge when I sign in"), and
inbound firewall rules for TCP 47820 and UDP 47821 on **private** networks
only. If TCP 47820 is taken and Bridge falls back to another port, Windows
asks about the firewall for that port. `Publisher` must match the signing
certificate's subject. Versions: `<Version>` in `Bridge.Windows.csproj`,
`Identity Version` in the manifest, and `versionName`/`versionCode` in
`android/app/build.gradle.kts` move together (0.2.0 now).

Icons are generated from one design by `tools/icons/generate_icons.py`
(Pillow): every Windows scale/targetsize PNG, `Bridge.ico` (window, taskbar
and tray), and the path data for the Android vector icons (adaptive, themed
and status-bar). `--check` verifies the committed assets.

## Running the vertical slice locally

1. Launch the Windows app — a short first-run guide, then a QR code. Allow it through Windows
   Firewall on **private** networks when prompted (it listens on TCP 47820
   and, for discovery, UDP 47821).
2. Launch the Android app on a phone on the **same Wi‑Fi network** as the PC,
   go through the three-step guide and allow camera access.
3. Scan the QR code. The phone pairs, then connects; the PC switches to the
   dashboard.
4. Drag files or folders onto the Windows dashboard to send them to the phone
   (saved in the app's `Android/data/com.bridge.app/files/Bridge` folder), or
   use Android's share sheet → Bridge (files) or "Send a folder" on the
   dashboard to send to the PC (saved in `Downloads\Bridge`). Turn Wi-Fi off
   and on mid-transfer: it pauses and continues where it stopped.
5. Turn on "Sync clipboard" on both sides to test clipboard updates. On
   Android 10+, copies made on the phone are only sent while Bridge is open
   (a platform privacy rule); PC → phone works in the background.
6. Tap "Show phone notifications on your PC" on Android and grant
   notification access to mirror notifications. On the PC, a mirrored
   message has a reply box and the app's own buttons (e.g. "Mark as read"),
   plus Dismiss; right-click (or "…") for "Stop <app> notifications". On the
   phone, "Choose apps" picks which apps appear on the PC.

## Testing

- `Bridge.Tests` (xUnit) — protocol serialization (camelCase wire format),
  crypto (transcript binding, replay/tamper rejection, signatures), pairing
  rules (token expiry, single use, key proof), safe file names, and end-to-end
  host tests over loopback TCP: pair → reconnect → files/clipboard/status,
  impersonation and revocation; robustness (randomly timed mid-send
  cancellation, connection loss during a send, a throwing clipboard, oversized
  pre-auth frames, idle connection floods, disk failure mid-receive, bidi
  file-name spoofing).
- `android/app/src/test` (JUnit, runs on the JVM) — protocol (de)serialization,
  envelope fields, safe file names, crypto and signatures, plus phone-side
  failure paths against `TestPc` (a minimal PC written from protocol.md):
  pairing timeouts and friendly errors, the dead-PC watchdog, a PC that
  rejects a file mid-upload, a PC that can't prove its key, oversized
  handshake frames, failing handlers, and long non-ASCII file names. Run the
  JVM with a UTF-8 locale (`LC_ALL=C.UTF-8`); with an ASCII locale Java
  silently mangles non-ASCII file names and those tests prove nothing.
- Cross-platform: the handshake, crypto and messages were verified by running
  the Windows `BridgeHost` against the Android `PairingClient` /
  `BridgeClientSession` over TCP (pairing, forged-PC rejection, wrong and
  reused tokens, files and clipboard in both directions, status,
  notifications, reconnect, impostor rejection and revocation; 20 MB and
  empty files both ways, a cancelled upload, nine concurrent uploads against
  the PC's limit of eight, and JSON-schema validation of real messages from
  both platforms).
- Discovery: `DiscoveryTests` (xUnit) and `DiscoveryTest` (JUnit) cover the
  wire format, privacy (silent for other PCs' probes), forged/replayed
  replies, rate limiting, floods, the reconnect loop's backoff/wake/retry
  rules; cross-platform, the Windows host moves to a new port after pairing
  and the Android reconnect loop finds it again over loopback and over real
  UDP broadcast (including a new IP address).
- Resume and folders: `ResumeAndFolderTests`/`ResumeHardeningTests` (xUnit)
  and `ResumeTest`/`ResumeHardeningTest` (JUnit) cover resuming after a cut
  and after an app restart, lost acks (no duplicates), changed source files,
  per-device partial isolation, cancel, expiry and caps, hostile folder
  paths, interrupted folders resuming into the same folder, the state store's
  write cost on 5,000-item queues, and crash-safe journals. Cross-platform
  (`e2e-resume`): 30 MB files resumed both ways (one into a restarted phone
  app), folders both ways with non-ASCII names, and an interrupted folder.
- Polish: `PolishTests` (xUnit) and `PolishTest` (JUnit) cover the
  diagnostics log (only safe field shapes, rotation and caps, retention,
  disabled/cleared, concurrent writers, unwritable folders), a real session
  logged end to end with no names, contents or keys in the log or report,
  settings (defaults, persistence, corruption, folder validation, unwritable
  storage), a download-folder change applying to a live session, recent
  transfers with Retry/Open rules, retry copies kept/deleted/swept on the
  phone, and the reconnect loop's failure/countdown hooks. The resume and
  notification E2E runs also log on both sides and fail if a file name,
  folder name, notification text or device name appears in either log.
- Notification actions: `NotificationActionTests` (xUnit) and
  `NotificationActionsTest` (JUnit) cover what is mirrored and what isn't,
  button ids and stale buttons, reply/press/dismiss/mute and their refusals,
  per-app choices and their storage, updates that stop qualifying, removals
  after an app restart, the PC waiting for a reconnecting phone, toast
  argument encoding, untrusted text, and random/malformed messages both ways.
  Cross-platform (`e2e-notifications`): the Windows host and the phone session
  exchange real posts, replies, button presses, dismissals and mutes,
  including a click made while the phone was disconnected and a phone app
  restart; messages from both platforms are checked against the schemas.
- Manual/integration still needed on real devices: the new WinUI and Compose
  screens (first-run guides, Settings, Recent transfers), start at sign-in
  (unpackaged and MSIX), a second launch handing over to the running Bridge,
  installing the MSIX (firewall rules, toast activation), the icons on
  Windows (Start, taskbar, tray) and Android (launcher, themed icon, status
  bar), sharing the diagnostics report, replying from Windows
  toasts in a few messaging apps (WhatsApp, Messages, Telegram, Gmail),
  toast buttons after Bridge was closed (Windows relaunches it), Android folder picker and
  copying large folders to cache, resume over a real Wi-Fi drop, discovery on real Wi-Fi
  (some routers block broadcast between clients), camera QR scanning,
  reconnection after Wi‑Fi drop, large-file transfer, clipboard sync across a
  few Android OEMs, Windows notifications and tray behaviour.

## Roadmap

See section 22 of the product blueprint for phased delivery
(architecture → pairing → file transfer → clipboard → notifications →
polish), and section 23 for the post-MVP roadmap (photos, Explorer
integration, handoff, calling, screen mirroring).
