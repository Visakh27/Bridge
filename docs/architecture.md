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
- **Handoff (links and text)** — `HandoffRules` (link/text checks, ASCII host,
  title cleaning) and the receiving/sending logic are plain code on both
  sides: `Bridge.Core` `HandoffService` and Android `handoff/HandoffInbox`
  (de-duplicated ids, a 20-a-minute limit, waiting for the answer). The
  Windows app turns what arrives into toasts (Open / Copy link) or opens the
  browser, and sends from the Share menu and the tray's "Send clipboard to
  phone"; Android's `HandoffNotifier` posts the link notification or sets the
  clipboard, and `MainActivity` sends text and links shared from other apps.
- **Several devices** — Windows: `BridgeHost` keeps one session per connected
  phone (at most 8) and an *active* phone (`CurrentSession`: the one the user
  picked, else the most recently connected) that sends, photos, handoffs and
  the tray act on; `SessionFor(deviceId)` reaches a particular phone.
  `ClipboardSyncService` sends the PC's clipboard to every connected phone
  with sync and never relays one phone's clipboard to another;
  `NotificationActionRouter` and `NotificationToastCommand` carry the phone a
  notification came from, so a reply goes back to it (waiting for that phone,
  never another); `PhotoBrowser` and `HandoffService` accept answers only from
  the phone that was asked; `ExplorerSendQueue` keeps a waiting send for the
  phone the user picked. Android: `network/PcChooser` orders the paired PCs
  (the user's choice, the last one used, then newest pairing) and
  `ReconnectLoop` tries them in turn, one connection at a time, forgetting
  only a PC that says it no longer knows the phone.
- **Remote access** — `Bridge.Network.RelayProtocol` (relay link, rooms, join
  proofs, the join line) is shared by the PC and the relay; Android has the same
  in `network/Relay.kt` (checked against the same test vectors).
  `Bridge.Relay` is a separate, self-hosted .NET service (`RelayServer`: rooms,
  limits, the byte pipe) with a Dockerfile. On the PC, `Bridge.Core`
  `RemoteAccess` keeps a connection waiting at the relay for each paired phone
  and hands a joined one to `BridgeHost.HandleConnectionAsync` (route
  `relay`) — from there it is an ordinary session; it also sends `relay.config`.
  On the phone, `ReconnectLoop` tries a PC's relay (via a `BridgeClientSession`
  whose connect step is `RelayClient.join`) only after the direct address and
  discovery failed, and the service moves back to a direct connection when the
  network changes. Bridge.Windows keeps the relay link encrypted with DPAPI.
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
`PERMISSION_DENIED`, `TOO_MANY_DEVICES` — carried in `error` messages as `{"code","message"}` and
translated to user-friendly text at the UI layer; raw exceptions/socket errors
are never shown to the user.

## Why no cloud backend in the MVP

All core features work over a direct local connection. Remote connectivity
is available without a Bridge-run backend: the user can run their own relay
(`docs/remote.md`), which only forwards end-to-end encrypted connections. A
future optional backend (section 19 of the blueprint) would only add
account/device management, push, update distribution and diagnostics — never
a requirement for core file/clipboard/notification sync.
