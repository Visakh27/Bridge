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

Every frame on the TCP connection is `[4-byte big-endian length][bytes]`
(maximum 16 MiB). The PC listens on TCP **47820** (falling back to a free
port if that one is taken); the port in use is in the pairing QR code.

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
(or `error` with `DEVICE_UNPAIRED` / `AUTH_FAILED` and closes) → either side
may now send any message the peer advertised.

## QR code

JSON (camelCase): `{"pairingToken","host","port","deviceId","deviceName","publicKeyBase64"}`.
`publicKeyBase64` is the PC's identity key.

## Message types (MVP)

| Type | Direction | Purpose |
|---|---|---|
| `pairing.request` | A→W | `{"pairingToken","deviceName","publicKeyBase64"}` (phone identity key) |
| `pairing.accept` / `pairing.reject` | W→A | `{"deviceName","publicKeyBase64"}` / `{"reason"}` |
| `capabilities.advertise` | both | `{"features": ["files","clipboard","notifications","photos"]}` |
| `presence.ping` / `presence.pong` | both | `{}` — keep-alive; each side pings every 15 s and drops the connection after 45 s of silence |
| `device.status` | A→W | `{"batteryPercent","charging","connectionQuality"}` |
| `clipboard.update` | both | `{"format":"text","data":"..."}` (≤ 10,000 chars) |
| `file.start` | both | `{"transferId","fileName","size","sha256","totalChunks"}` |
| `file.chunk` | both | `{"transferId","index","data"}` (base64, indexes strictly in order from 0) |
| `file.end` | both | `{"transferId"}` |
| `file.ack` | both | `{"transferId","status":"ok"|"hash_mismatch"|"cancelled"}` |
| `file.cancel` | both | `{"transferId"}` — either side, for a transfer in either direction |
| `notification.post` | A→W | `{"appName","title","text","key"}` |
| `notification.dismiss` | A→W | `{"key"}` |
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
characters are replaced, and an existing file is never overwritten
("name (1).ext"). Out-of-order chunks, more bytes than `size`, or a SHA-256
mismatch fail the transfer with `file.ack` `hash_mismatch`.
