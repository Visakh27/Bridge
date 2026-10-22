# Bridge — Android ↔ Windows Continuity Platform

Working implementation of the "Recommended First Build" vertical slice from
the product blueprint: QR pairing, a mutually authenticated ephemeral-ECDH +
AES-256-GCM encrypted channel over local TCP, private local discovery of a
paired PC whose address changed, bidirectional chunked/hash-verified file and
folder transfer that resumes after a dropped connection, text clipboard sync,
a Windows system-tray app, an Android foreground connection service, and
notification mirroring with actions (reply, the app's own buttons, dismiss,
per-app filtering), browsing the phone's photos from the PC (opt-in), File Explorer "Send to" /
"Share" to the phone, first-run guides,
settings, a privacy-safe diagnostics log,
recent transfers with retry, and MSIX packaging — all speaking one shared, versioned JSON protocol
(`protocol/protocol.md`).

```
bridge/
├── windows/        Bridge.Windows (WinUI 3 app + MSIX manifest), Bridge.Core,
│                   Bridge.Network, Bridge.Protocol, Bridge.Security, Bridge.Tests
├── android/        Kotlin + Jetpack Compose app (app/src/main/java/com/bridge/app/...)
├── protocol/       protocol.md + JSON schemas — the contract both platforms implement
└── docs/           architecture.md, security.md, development.md
```

See `docs/development.md` for build/run instructions, `docs/architecture.md`
for how the pieces fit together, and `docs/security.md` for the crypto and
privacy model.

## What's here vs. what's left

This is a genuine starting implementation, not a stub: the protocol,
authenticated handshake, session encryption, pairing, reconnect, file
transfer, clipboard sync, status, notification mirroring and actions, and local
discovery of a paired PC whose address changed are implemented
on both sides and interoperate (Windows uses .NET's ECDH/ECDSA/HKDF/AES-GCM,
Android uses the JCE equivalents with matching curve/labels/framing — see
`Bridge.Security.SessionCrypto` / `security/SessionCrypto.kt`). The
Windows ↔ Android wire compatibility has been exercised end to end by running
the Windows `BridgeHost` against the Android client code over TCP. What's
intentionally left as follow-up work, per the blueprint's own phased roadmap:

- A production **Room/SQLite**-backed `PairedDeviceStore` on Android (a
  SharedPreferences store is used now) and swapping Windows' JSON-file store
  for SQLite per section 5/14.
- A virtual phone drive in Explorer (Send to/Share and Quick access exist),
  **calls**, and **screen mirroring** — all explicitly Phase 2+ / post-MVP in
  the blueprint (sections 12–13, 22–23).
- Store submission (the MSIX is sideload-signed with a dev certificate;
  Android release signing is not set up).

## Building

Windows needs Visual Studio 2022 with the WinUI 3 workload (build for x64,
x86 or ARM64); Android needs Android Studio (`./gradlew assembleDebug`). See
`docs/development.md` for exact steps. Unit tests (`Bridge.Tests` for .NET,
`app/src/test` for Kotlin) cover the wire format, crypto, pairing rules, safe
file handling and full pair → reconnect → transfer sessions over loopback, and
are a good first sanity check after opening each project.
