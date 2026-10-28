# Bridge Wire Protocol (v1)

All messages are UTF-8 JSON with **camelCase** keys, sent as encrypted frames
over the transport (TCP initially) **after** the authenticated handshake below
completes. Every message carries `protocol`, `type`, `id`, `timestamp` and a
`payload` object (`{}` when there is nothing to say). Receivers ignore
unknown keys and unknown message types.

```json
{
  "protocol": 1,
  "type": "clipboard.update",
  "id": "5f9c2b3a-...-uuid",
  "timestamp": "2026-09-24T10:15:00Z",
  "payload": { "format": "text", "data": "..." }
}
```

## Transport framing

Every frame on the TCP connection is `[4-byte big-endian length][bytes]`.
Receivers enforce a maximum that grows with trust: **4 KiB** for handshake
frames, **64 KiB** on the PC until it has verified who the phone is, and
**16 MiB** once both sides are authenticated. A larger length prefix closes
the connection without reading the body. The PC listens on TCP **47820** (falling back to a free
port if that one is taken); the port in use is in the pairing QR code and is
reported by discovery (below).

## Keys

- **Identity key** — long-term NIST P-256 key per device, used only for
  ECDSA-SHA256 signatures (DER-encoded). Public keys travel as base64
  X.509 SubjectPublicKeyInfo. Windows: DPAPI-protected file; Android: Android
  Keystore (non-exportable).
- **Ephemeral key** — fresh P-256 ECDH key per connection.

## Handshake (raw frames, before any JSON)

The phone is always the initiator. Inside each frame, every field is
`[4-byte big-endian length][bytes]`.

| Step | Direction | Fields |
|---|---|---|
| `HELLO` | phone → PC | phone device id (UTF-8), phone ephemeral key |
| `HELLO_ACK` | PC → phone | PC device id, PC ephemeral key, PC signature |
| `AUTH` | phone → PC | phone signature |

Both signatures are over the **transcript**
`T = SHA-256( f("bridge-handshake-v1") ‖ f(phoneId) ‖ f(phoneEphemeral) ‖ f(pcId) ‖ f(pcEphemeral) )`,
where `f(x)` is the 4-byte big-endian length of `x` followed by `x`.

- The phone verifies the PC's signature against the PC identity key it
  expects (from the QR code when pairing, from the stored pairing when
  reconnecting) and **aborts before sending anything else** if it fails. This
  is what stops a machine on the same network impersonating the PC.
- The PC verifies the phone's signature against the stored key of the paired
  device with that id, or, during pairing, against the key carried in
  `pairing.request`.

Session keys: `Z` = raw ECDH shared secret (x-coordinate), then
HKDF-SHA256 with `salt = T`, 32-byte output, and
`info = "bridge-v1 initiator->responder"` (phone → PC) or
`"bridge-v1 responder->initiator"` (PC → phone).

Encrypted frames are `[12-byte nonce][ciphertext][16-byte GCM tag]` with
AES-256-GCM. The nonce is 4 zero bytes followed by a 64-bit big-endian
counter that starts at 0 for each direction and increases by one per frame.
A receiver rejects any frame whose nonce isn't the next expected value
(replay, drop or reorder) and closes the connection.

## Connection flows

**Pairing** (phone has just scanned the QR code):
handshake → phone sends `pairing.request` → PC replies `pairing.accept` or
`pairing.reject` → connection closes. The token is single-use and expires
after 5 minutes.

**Session** (paired phone connecting or reconnecting):
handshake → phone sends `capabilities.advertise` → PC verifies the phone is
paired and its signature checks out, then replies `capabilities.advertise`
(or `error` with `DEVICE_UNPAIRED` / `AUTH_FAILED` / `TOO_MANY_DEVICES` and
closes) → either side may now send any message the peer advertised.

**Several devices.** A PC accepts sessions from several paired phones at once
(at most 8; a ninth gets `TOO_MANY_DEVICES`, which the phone treats like an
unreachable PC: it tries another paired PC or retries later, without running
discovery). Each phone has one session; a phone reconnecting replaces its own
stale session only. A phone paired with several PCs keeps one session at a
time. Nothing on the wire changes: every message belongs to the session it
arrives on, so request ids, notification keys, transfer ids and handoff ids
need only be unique per session, and an answer counts only when it arrives on
the session the request was sent on.

## Discovery (UDP 47821)

The QR code holds the PC's address at pairing time. If a paired phone later
can't connect there (the PC's IP changed, or it fell back to another TCP
port), it looks for that PC on the local network. Discovery is for finding a
PC you have already paired with; pairing itself always uses the QR code.

Datagrams are UTF-8 JSON (camelCase), at most 512 bytes
(`protocol/schemas/discovery.schema.json`):

| Datagram | Direction | Fields |
|---|---|---|
| probe | phone → subnet broadcast and 255.255.255.255, port 47821 | `{"bridge":"discover","v":1,"nonce","tag"}` |
| reply | PC → the probe's source address/port (unicast) | `{"bridge":"here","v":1,"nonce","deviceId","port","sig"}` |

- `nonce`: 16 random bytes (base64), fresh for every search.
- `tag` = first 16 bytes of `SHA-256( f("bridge-discovery-tag-v1") ‖ f(pcIdentityKey) ‖ f(nonce) )`.
  A PC answers **only** if the tag matches its own identity key, so PCs never
  announce themselves, and an observer who doesn't know a PC's public key
  can't tell which PC a probe is for or link two probes together.
- `port`: the PC's current TCP port. `sig`: ECDSA-SHA256 (DER, base64) by the
  PC identity key over `f("bridge-discovery-v1") ‖ f(nonce) ‖ f(deviceId) ‖ f(port as 4-byte BE)`.
- The phone accepts a reply only if the nonce is its own, `deviceId` is the
  paired PC's, and the signature verifies under the stored PC key. The PC's
  address is taken from the reply's source IP. The phone saves the new
  address and connects; the authenticated handshake still applies.
- The phone resends the probe at 0, 0.3 and 0.8 s and waits up to 2.5 s. The
  PC answers at most 20 probes per second and ignores anything malformed.

When the phone retries: after a failed connection to the stored address
(unreachable, timed out, or a machine that can't prove the PC's key), the
phone runs discovery. If the PC answers from a different address or port, the
phone retries there immediately, at most once per failure; otherwise the
normal backoff (1, 2, 5, 10, 30 s) applies. A network change (Wi-Fi
reconnects) ends the backoff early, and losing the network drops the dead
connection at once.

## QR code

JSON (camelCase): `{"pairingToken","host","port","deviceId","deviceName","publicKeyBase64"}`.
`publicKeyBase64` is the PC's identity key.

## Message types (MVP)

| Type | Direction | Purpose |
|---|---|---|
| `pairing.request` | A→W | `{"pairingToken","deviceName","publicKeyBase64"}` (phone identity key) |
| `pairing.accept` / `pairing.reject` | W→A | `{"deviceName","publicKeyBase64"}` / `{"reason"}` |
| `capabilities.advertise` | both | `{"features": ["files","clipboard","notifications","files.resume","files.folders","notifications.actions","photos","handoff","remote"]}` |
| `presence.ping` / `presence.pong` | both | `{}` — keep-alive; each side pings every 15 s and drops the connection after 45 s of silence |
| `device.status` | A→W | `{"batteryPercent","charging","connectionQuality"}` |
| `clipboard.update` | both | `{"format":"text","data":"..."}` (≤ 10,000 chars) |
| `file.start` | both | `{"transferId","fileName","size","sha256","totalChunks"}` + optional `resumable`, `relativePath`, `batchId`, `batchIndex`, `batchCount` (see Resume, Folders) |
| `file.resume` | both | `{"transferId","nextIndex"}` — answer to a resumable `file.start` |
| `file.chunk` | both | `{"transferId","index","data"}` (base64, indexes strictly in order from 0) |
| `file.end` | both | `{"transferId"}` |
| `file.ack` | both | `{"transferId","status":"ok"|"hash_mismatch"|"cancelled"}` |
| `file.cancel` | both | `{"transferId"}` — either side, for a transfer in either direction |
| `notification.post` | A→W | `{"appName","title","text","key"}` + optional `appId`, `actions`, `canDismiss` (see Notification actions) |
| `notification.dismiss` | both | `{"key"}` — A→W: it's gone from the phone; W→A: clear it on the phone |
| `notification.action` | W→A | `{"requestId","key","actionId"}` + `replyText` for reply buttons |
| `notification.action.result` | A→W | `{"requestId","status":"ok"|"gone"|"refused"|"invalid"|"failed"}` |
| `notification.mute` | W→A | `{"appId"}` — stop mirroring that app |
| `photos.list` / `photos.page` | W→A / A→W | `{"requestId","limit","before"?}` / `{"requestId","status","items","next"?}` (see Photos) |
| `photos.thumb` / `photos.thumbnail` | W→A / A→W | `{"requestId","id"}` / `{"requestId","id","status","mime"?,"data"?}` |
| `photos.fetch` / `photos.fetched` | W→A / A→W | `{"requestId","id"}` / `{"requestId","id","status","transferId"?}` |
| `photos.changed` | A→W | `{}` — the phone's photos changed; refresh |
| `handoff.open` | both | `{"id","kind":"url"|"text","url"?,"title"?,"text"?}` (see Handoff) |
| `handoff.ack` | both | `{"id","status"}` — `shown`, `opened`, `copied`, `disabled`, `invalid`, `busy` |
| `relay.config` | W→A | `{"enabled":true,"host","port","key"}` or `{"enabled":false}` (see Remote access) |
| `error` | both | `{"code","message"}` — see docs/architecture.md error codes |

## Versioning

Clients exchange `capabilities.advertise` as the first message of every
session. A client MUST NOT send a message type the peer hasn't advertised
support for; it should degrade gracefully instead (e.g. skip clipboard sync
if peer lacks `"clipboard"`).

## Files

Large payloads (files) are chunked client-side into 64 KB pieces *before*
base64 encoding, so each `file.chunk` message stays well under typical socket
buffer sizes. `file.start.totalChunks` lets the receiver show progress and
detect truncated transfers (an empty file has `totalChunks: 0`). Receivers
treat `fileName` as untrusted: only its last path segment is used, unsafe
characters are replaced, invisible Unicode format characters (such as the
right-to-left override used to disguise `.exe` as `.png`) are removed, the
name is shortened to fit the local filesystem (200 UTF-8 bytes on Android,
200 UTF-16 units on Windows, keeping the extension), and an existing file is
never overwritten ("name (1).ext"). A receiver that can't store a file (disk
full, storage removed) answers `file.ack` `cancelled` right away; a sender
that receives any non-`ok` ack or a `file.cancel` stops sending.

Cancelling a transfer never abandons a frame half-written: an encrypted
frame consumes a nonce, so senders only stop between frames, then send
`file.cancel`. Out-of-order chunks, more bytes than `size`, or a SHA-256
mismatch fail the transfer with `file.ack` `hash_mismatch`.

Optional fields are omitted when unused (never sent as `null`).

## Resume (`files.resume`)

Used only when the receiver advertised `files.resume`.

- The sender sets `"resumable": true` in `file.start` and waits up to 30 s for
  the receiver's answer before sending any chunk:
  - `file.resume {"transferId","nextIndex"}` — send chunks from `nextIndex`
    (0 for a new file);
  - `file.ack "ok"` — the receiver already stored this exact file (same
    transfer id **and** SHA-256) on an earlier connection whose ack was lost:
    nothing to send;
  - `file.ack "cancelled"` — refused (busy, no space).
- The receiver keeps a resumable partial file across disconnects for 24 h,
  keyed by (sending device, transfer id), and reuses it only if `fileName`,
  `size`, `sha256`, `totalChunks` and the folder fields are identical. Only
  whole chunks count; a half-written last chunk is redone. Each peer can
  leave at most 32 abandoned partials (oldest are evicted).
- The sender keeps every resumable send in an outbox until it receives
  `file.ack "ok"` after `file.end`. If the connection drops first the send is
  *interrupted* (not "sent"); on the next connection the sender offers each
  outbox entry again with the same transfer id. A source file that changed
  (different size, timestamp or hash) simply starts from 0 on the receiver.
- Explicit cancellation (`file.cancel`, user cancel) or a failed check
  (`hash_mismatch`) deletes the partial and the outbox entry; only a lost
  connection is resumable. The final SHA-256 check always covers the whole
  file, including chunks received on earlier connections.

## Folders (`files.folders`)

Used only when the receiver advertised `files.folders`; otherwise the files
of a folder are sent as loose files.

- Each file of a folder is a normal transfer whose `file.start` carries
  `relativePath` (its folder, '/'-separated, starting with the top folder's
  name, e.g. `"Holiday/day 1"`), a shared `batchId`, and
  `batchIndex`/`batchCount` for progress ("file 12 of 340"). Files are sent
  one at a time; empty folders are not sent.
- The receiver treats the path as untrusted: every segment is sanitised like
  a file name, `.`/`..`/empty segments are dropped, more than 16 levels is
  refused (`file.ack "cancelled"`), and the result must stay inside the
  download folder. If a folder with the top name already exists, the whole
  batch goes into `Name (1)` (remembered per batch, so a resumed folder
  never splits).
- At most 10,000 files per folder.


## Notification actions (`notifications.actions`)

Used only when the other side advertised `notifications.actions`: the phone
then adds the optional fields to `notification.post`, and the PC may send the
W→A notification messages. Schemas: `notification_post`, `notification_action`,
`notification_action_result`, `notification_mute` in `protocol/schemas`.

- `notification.post` extras: `appId` (the Android package name),
  `actions` — up to 3 of the notification's own buttons,
  `[{"id","title","reply":true?}]` (`reply` marks a button that takes typed
  text) — and `canDismiss` (the user can clear it on the phone). Buttons that
  would open an app on the phone, system-suggested smart actions and
  choice-only inputs are not offered. An update that changes nothing the PC
  shows is not sent again.
- Button ids are `"<index>-<8 hex>"` (the hex is from the button's title and
  kind). If the app changes the notification's buttons, an old id matches
  nothing and the phone answers `gone`; it never presses a different button.
- `notification.action`: the phone answers `notification.action.result` with
  the same `requestId`:
  `ok` — done; `gone` — the notification or that button no longer exists;
  `refused` — Bridge doesn't mirror it (the app is turned off, or it is an
  ongoing/secret notification); `invalid` — `replyText` missing, blank or over
  2,000 characters on a reply button, or present on a plain button;
  `failed` — the app didn't accept it. The PC gives up after 15 s.
- `notification.dismiss` (W→A) clears the notification on the phone if it is
  mirrored and clearable; the phone then sends its usual `notification.dismiss`
  (A→W). If it's already gone, the phone answers with `notification.dismiss` straight away.
- `notification.mute` turns the app off in the phone's per-app list; the phone
  sends `notification.dismiss` for each of that app's notifications the PC is
  showing. Only the phone can turn an app back on.
- The phone acts only on notifications that are showing now *and* that it
  would mirror; every request is checked again against the live notification.

## Photos (`photos`)

The phone advertises `photos` when it can answer photo requests; the PC
advertises it when it can browse (the phone sends `photos.changed` only to
such a PC). Schemas: `photos_*.schema.json`.

- Off by default on the phone: until the user turns on "Let your PC browse
  photos", every request is answered `disabled`; without Android's photo
  permission, `no_permission`. Android 14 "selected photos" access limits the
  PC to those photos.
- `photos.list`: newest first, at most `limit` (1–100) items. `before` is the
  `next` cursor of the previous page (`"<sortKey>:<id>"`, digits only); a page
  without `next` is the last. Items: `{"id","name","takenAt","width","height","size","mime"}`
  — `id` is the phone's library id (digits), `takenAt` epoch ms.
- `photos.thumb`: a JPEG preview at most 320 px on its long side and 256 KB.
- `photos.fetch`: the phone answers `photos.fetched` with `ok` and a
  `transferId`, then sends the original as a normal file transfer with that id
  (resumable, so it continues after a dropped connection).
- Photos are addressed only by library id; the phone never takes a path. It
  answers every request (`ok`, `disabled`, `no_permission`, `not_found`,
  `busy` when more than 64 are waiting, `invalid`, `error`), working off its
  receive loop so file transfers aren't held up. The PC matches answers by
  `requestId`, ignores anything it didn't ask for, gives up after 20 s, and
  checks previews (JPEG marker, size) and items (digit ids) before use.
- `photos.changed` is sent at most once per 2 s of quiet after the phone's
  library changes.

## Handoff (`handoff`)

Either side advertises `handoff` when it accepts links and text; a side sends
`handoff.open` only to a peer that advertised it. Schemas: `handoff_*.schema.json`.

- `kind: "url"` carries `url` (and optionally `title`, ≤ 200 chars, for
  display only); `kind: "text"` carries `text` (1–10,000 chars). `id` (1–64
  chars, unique per sender) ties the answer to the request.
- Links must be absolute `http` or `https` URLs of at most 2048 characters,
  with a host, without user info (`user:pass@`), whitespace or control
  characters. Anything else is answered `invalid` and never opened —
  `javascript:`, `file:`, `intent:`, `ms-settings:` and similar schemes in
  particular. Receivers check again on their own side, display the host
  (international names as punycode), and strip control and bidi-override
  characters from titles.
- The receiver answers every `handoff.open` with `handoff.ack`:
  `shown` (a notification offers to open the link), `opened` (opened at once;
  only on the PC, and only when the user turned that on), `copied` (text is
  on the clipboard), `disabled` (the user turned handoff off), `invalid`, or
  `busy` (more than 20 in a minute). A repeated `id` gets the same answer and
  nothing is shown twice. The phone never opens a link by itself.
- The sender waits up to 10 s for the answer; a status it doesn't know counts
  as `invalid`. Handoffs are not queued while disconnected.
- Diagnostics record only direction, kind and status — never the link or text.

## Remote access (`remote`, relay)

The phone advertises `remote` when it can connect through a relay. A PC with
remote access on sends such a phone `relay.config` over their authenticated
session (on connect and whenever it changes): the relay's `host`, `port` and
`key` (32 bytes, base64url), or `{"enabled":false}`. Schema:
`relay_config.schema.json`. The phone validates it, keeps it for that PC only,
and ignores a malformed update.

**Relay join protocol** (plain TCP to the relay, before any Bridge framing).
The client sends one UTF-8 JSON line (≤ 1024 bytes, `\n`-terminated):

```
{"v":1,"role":"pc"|"phone","room":"<64 hex>","ts":<unix seconds>,"nonce":"<32 hex>","proof":"<64 hex>"}
```

- `room` = lowercase hex SHA-256 of `"bridge-relay-room-v1" 0x00 <PC public key> 0x00 <phone public key>`,
  using the long-term public keys exactly as exchanged at pairing.
- `proof` = lowercase hex HMAC-SHA256(relay key, `"bridge-relay-v1\n" role "\n" room "\n" ts "\n" nonce`).
  The relay accepts a proof once (by nonce) and only within 2 minutes of `ts`.

The relay replies with JSON lines: `{"t":"waiting"}` (to a PC), `{"t":"ping"}`
(every 25 s to a waiting PC), `{"t":"paired"}`, or
`{"t":"error","code":"bad_hello|unauthorized|pc_offline|busy|replaced"}`
followed by a close. After `paired` the connection is a plain byte pipe to the
other side, and the normal session starts on it: framing, the handshake with
the phone as initiator, then `capabilities.advertise`. The relay never sees
keys or plaintext. A PC keeps one waiting connection per paired phone, and a
newer one from the same room replaces the older (`replaced`). A phone with no
PC waiting gets `pc_offline`. A PC refuses `pairing.request` on a connection
that came through the relay (`error` `PERMISSION_DENIED`): pairing is
local-only.

A phone tries the relay for a PC only when that PC wasn't reachable directly
(at its stored address, then via discovery). A PC that answered but refused
(for example `TOO_MANY_DEVICES`) isn't retried through the relay.

