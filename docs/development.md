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

`Bridge.Windows` runs unpackaged by default. To produce an MSIX, set
`WindowsPackageType` to `MSIX` in `Bridge.Windows.csproj` (the manifest is
`Bridge.Windows/Package.appxmanifest`) or use Publish › Create App Packages.

## Running the vertical slice locally

1. Launch the Windows app — it shows a QR code. Allow it through Windows
   Firewall on **private** networks when prompted (it listens on TCP 47820).
2. Launch the Android app on a phone on the **same Wi‑Fi network** as the PC
   and allow camera access.
3. Scan the QR code. The phone pairs, then connects; the PC switches to the
   dashboard.
4. Drag a file onto the Windows dashboard to send it to the phone (saved in
   the app's `Android/data/com.bridge.app/files/Bridge` folder), or use
   Android's share sheet → Bridge to send files to the PC (saved in
   `Downloads\Bridge`).
5. Turn on "Sync clipboard" on both sides to test clipboard updates. On
   Android 10+, copies made on the phone are only sent while Bridge is open
   (a platform privacy rule); PC → phone works in the background.
6. Tap "Show phone notifications on your PC" on Android and grant
   notification access to mirror notifications.

## Testing

- `Bridge.Tests` (xUnit) — protocol serialization (camelCase wire format),
  crypto (transcript binding, replay/tamper rejection, signatures), pairing
  rules (token expiry, single use, key proof), safe file names, and end-to-end
  host tests over loopback TCP: pair → reconnect → files/clipboard/status,
  impersonation and revocation.
- `android/app/src/test` (JUnit, runs on the JVM) — protocol (de)serialization,
  envelope fields, safe file names, crypto and signatures.
- Cross-platform: the handshake, crypto and messages were verified by running
  the Windows `BridgeHost` against the Android `PairingClient` /
  `BridgeClientSession` over TCP (pairing, forged-PC rejection, wrong and
  reused tokens, files and clipboard in both directions, status,
  notifications, reconnect, impostor rejection and revocation).
- Manual/integration still needed on real devices: camera QR scanning,
  reconnection after Wi‑Fi drop, large-file transfer, clipboard sync across a
  few Android OEMs, Windows notifications and tray behaviour.

## Roadmap

See section 22 of the product blueprint for phased delivery
(architecture → pairing → file transfer → clipboard → notifications →
polish), and section 23 for the post-MVP roadmap (photos, Explorer
integration, handoff, calling, screen mirroring).
