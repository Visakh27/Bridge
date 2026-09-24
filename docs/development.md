# Development Guide

## Requirements

- **Windows**: Windows 11, Visual Studio 2022+ with the "Windows App SDK
  (WinUI 3)" and ".NET desktop development" workloads, .NET 8 SDK.
- **Android**: Android Studio (latest), JDK 17, Android SDK 34+.

## Getting started

```
git clone <repo>
cd bridge

# Windows
cd windows
dotnet restore Bridge.sln
dotnet build Bridge.sln
# or open Bridge.sln in Visual Studio and run Bridge.App

# Android
cd android
./gradlew assembleDebug
# or open android/ in Android Studio and run the `app` configuration
```

## Running the vertical slice locally

1. Launch the Windows app — it opens the pairing screen with a QR code.
2. Launch the Android app on a phone on the **same Wi‑Fi network** as the PC.
3. Scan the QR code from the Android app.
4. Once paired, drag a file onto the Windows dashboard to send it to the
   phone, or use Android's share sheet → Bridge to send a file to the PC.
5. Toggle clipboard sync in Settings on either side to test bidirectional
   clipboard updates.

## Testing

- `Bridge.Tests` (xUnit) — protocol (de)serialization, chunking, SHA-256
  verification, crypto round-trips.
- `android/app/src/test` (JUnit) — protocol (de)serialization, chunking,
  crypto round-trips.
- Manual/integration: pairing, reconnection after Wi‑Fi drop, large-file
  transfer resume, clipboard sync across a few Android OEMs.

## Roadmap

See section 22 of the product blueprint for phased delivery
(architecture → pairing → file transfer → clipboard → notifications →
polish), and section 23 for the post-MVP roadmap (photos, Explorer
integration, handoff, calling, screen mirroring).
