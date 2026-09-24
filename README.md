# Bridge — Android ↔ Windows Continuity Platform

Working implementation of the "Recommended First Build" vertical slice from
the product blueprint: QR pairing, local TCP discovery, an ephemeral-ECDH +
AES-256-GCM encrypted channel, bidirectional chunked/hash-verified file
transfer, text clipboard sync, a Windows system-tray app, an Android
foreground connection service, and notification mirroring — all speaking one
shared, versioned JSON protocol (`protocol/protocol.md`).

```
bridge/
├── windows/        Bridge.App, Bridge.Windows (WinUI 3), Bridge.Core,
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
handshake, session encryption, pairing, file transfer, and clipboard sync are
fully implemented and interoperate (Windows uses .NET's ECDH/HKDF/AES-GCM,
Android uses the JCE equivalents with matching curve/labels/framing — see
`Bridge.Security.SessionCrypto` / `network/SessionCrypto.kt`). What's
intentionally left as follow-up work, per the blueprint's own phased roadmap:

- Local device **discovery** (Bluetooth/mDNS) — pairing currently connects by
  scanning a QR code that encodes the PC's LAN IP directly; broadcast
  discovery for "already paired, reconnect automatically" is a Phase 1 item.
- **Reconnect wiring** on the Windows side beyond `ReconnectManager`'s skeleton.
- A production **Room/SQLite**-backed `PairedDeviceStore` on Android (an
  in-memory placeholder is used in `MainActivity` so the UI is runnable) and
  swapping Windows' JSON-file store for SQLite per section 5/14.
- Notification **actions** (reply/dismiss), **photos/Explorer** integration,
  **calls**, and **screen mirroring** — all explicitly Phase 2+ / post-MVP in
  the blueprint (sections 12–13, 22–23).
- App icons/branding assets (placeholders only).

## Building

Windows needs Visual Studio 2022 with the WinUI 3 workload; Android needs
Android Studio. Neither can be compiled or run inside this environment — see
`docs/development.md` for exact steps once you have the projects open
locally. Unit tests (`Bridge.Tests` for .NET, `app/src/test` for Kotlin) cover
protocol serialization and the crypto round-trip and are a good first sanity
check after opening each project.
