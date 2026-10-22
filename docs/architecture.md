# Bridge Architecture

```
Windows App (WinUI 3) ─┐                      ┌─ Android Companion (Compose)
                        │                      │
Bridge.Windows ─────────┤                      ├───────── ui/
Bridge.Core ────────────┤   Bridge Core        ├───────── core/
Bridge.Network ─────────┤   (shared concepts,  ├───────── network/
Bridge.Protocol ────────┤    mirrored per      ├───────── protocol/
Bridge.Security ────────┤    platform)         ├───────── security/
                        │                      ├───────── notifications/
                        └──────────────────────┴───────── service/
                     Protocol/Security layer (protocol/protocol.md)
                     Transport: TCP (LAN) now, QUIC/BLE later
```

## Layers

- **Protocol** — versioned JSON message types, identical semantics on both
  platforms (`protocol/protocol.md`). Neither platform should encode
  platform-specific assumptions into a message payload.
- **Security** — P-256 identity keys (ECDSA) and ephemeral ECDH key exchange
  with a signed transcript, AES-256-GCM session encryption with counter
  nonces, SHA-256 file integrity. See `docs/security.md`.
- **Network/Transport** — TCP over the local network for MVP (PC listens on
  47820), with the transport abstracted behind `ITransport` on Windows so
  QUIC or Bluetooth LE can be added later without touching protocol code.
- **Sessions** — on Windows, `Bridge.Core.BridgeHost` accepts connections,
  handles pairing and authenticates reconnecting phones, then hands each live
  connection to a `BridgeSession`; on Android, `network/BridgeClientSession`
  is the counterpart, wrapped by the `BridgeConnectionService` foreground
  service; its `ReconnectLoop` reconnects with backoff, reacts to network
  changes, and uses `PcDiscovery` (UDP 47821, answered by Windows'
  `DiscoveryResponder`) to re-find a PC whose address changed. Both run ping/pong keep-alives and drop a
  silent peer after 45 s.
- **Platform integrations** — Bridge.Windows (tray, dashboard, drag-drop,
  Windows Clipboard/Notification APIs) and the Android `ui/`, `notifications/`,
  `service/` packages (Compose UI, NotificationListenerService, foreground
  connection service).
- **Notifications** — on Android, `notifications/NotificationMirror` holds the
  rules (what is mirrored, per-app `NotificationFilter`, button ids, what the
  PC may do) as plain Kotlin; `BridgeNotificationListener` only converts
  StatusBarNotifications and carries out button presses/replies. On Windows,
  `BridgeSession` cleans incoming text (`PhoneNotificationText`) and tracks
  action requests; `NotificationActionRouter` carries out toast clicks, waiting
  briefly for the phone if it is reconnecting; `NotificationToastCommand` is
  what a toast button carries. Bridge.Windows only builds toasts and forwards
  clicks.

- **Photos** — Android: `photos/PhotoService` (plain Kotlin: validation,
  paging cursors, a bounded worker queue, change hints) over a `PhotoLibrary`,
  which is `MediaStorePhotoLibrary` on a device. Windows: `Bridge.Core`
  `PhotoBrowser` (request/answer matching, preview cache, waiting for a
  fetched original's transfer); Bridge.Windows' Photos view is a thumbnail
  grid over it.
- **File Explorer** — `Bridge.Core` `LaunchArguments` (Windows command-line
  splitting, `--send` paths) and `ExplorerSendQueue` (send now, or hold until
  the phone reconnects, with expiry and cancel); Bridge.Windows'
  `ExplorerIntegration` adds the Send to shortcut and the Quick access pin,
  and `App` routes Send to (first launch or a redirected second launch) and
  Share activations to the queue.
- **Polish (settings, diagnostics, history)** — Windows: `Bridge.Core`
  `SettingsStore` (validated, atomic JSON), `DiagnosticsLog` +
  `BridgeDiagnostics` (event wiring) + `DiagnosticsReport`, and
  `TransferHistory` (Recent transfers, Retry/Open); Bridge.Windows adds the
  first-run guide, the Settings panel, start-at-sign-in (`StartupRegistration`:
  Run key unpackaged, StartupTask in MSIX) and a single-instance entry point
  (`Program`). Android: `core/DiagnosticsLog` + `PhoneDiagnostics`,
  `network/TransferHistory`, `BridgePrefs`, and the Onboarding/Settings
  screens. The log, report and history are plain code tested on both sides.

## Error codes

`CONNECTION_TIMEOUT`, `AUTH_FAILED`, `DEVICE_UNPAIRED`, `TRANSFER_INTERRUPTED`,
`PERMISSION_DENIED` — carried in `error` messages as `{"code","message"}` and
translated to user-friendly text at the UI layer; raw exceptions/socket errors
are never shown to the user.

## Why no cloud backend in the MVP

All core features work over a direct local connection. A future optional
backend (section 19 of the blueprint) would only add account/device
management, push, update distribution, diagnostics and optional remote
connectivity — never a requirement for core file/clipboard/notification sync.
