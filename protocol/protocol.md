# Bridge Wire Protocol (v1)

All messages are UTF-8 JSON, newline-delimited over the transport (TCP initially),
sent **after** the session encryption handshake completes. Every message carries
`protocol`, `type`, `id`, and `timestamp`.

```json
{
  "protocol": 1,
  "type": "clipboard.update",
  "id": "5f9c2b3a-...-uuid",
  "timestamp": "2026-09-24T10:15:00Z",
  "payload": { "format": "text", "data": "..." }
}
```

## Handshake (pre-protocol, raw bytes)

1. `HELLO` — sender's ephemeral X25519 public key + device id.
2. `HELLO_ACK` — receiver's ephemeral X25519 public key + device id.
3. Both sides derive a shared secret (HKDF-SHA256) → AES-256-GCM session keys
   (separate keys per direction). All framed messages from here on are
   `[4-byte length][12-byte nonce][ciphertext+tag]`.

## Message types (MVP)

| Type | Direction | Purpose |
|---|---|---|
| `pairing.request` | A→W | Present pairing token scanned from QR |
| `pairing.accept` / `pairing.reject` | W→A | Accept/deny pairing, exchange long-term public keys |
| `capabilities.advertise` | both | `{"features": ["files","clipboard","notifications","photos"]}` |
| `presence.ping` / `presence.pong` | both | Keep-alive / reconnect detection |
| `device.status` | A→W | Battery level, charging state, connection quality |
| `clipboard.update` | both | `{"format":"text","data":"..."}` |
| `file.start` | both | `{"transferId","fileName","size","sha256","totalChunks"}` |
| `file.chunk` | both | `{"transferId","index","data"}` (base64) |
| `file.end` | both | `{"transferId"}` |
| `file.ack` | both | `{"transferId","status":"ok"|"hash_mismatch"|"cancelled"}` |
| `file.cancel` | both | `{"transferId"}` |
| `notification.post` | A→W | `{"appName","title","text","key"}` |
| `notification.dismiss` | A→W | `{"key"}` |
| `error` | both | `{"code","message"}` — see docs/architecture.md error codes |

## Versioning

Clients advertise `capabilities.advertise` right after `pairing.accept`. A newer
client MUST NOT send a message type the peer hasn't advertised support for; it
should degrade gracefully instead (e.g. skip clipboard sync if peer lacks
`"clipboard"`).

## Framing

Large payloads (files) are chunked client-side into ~64 KB pieces *before*
base64 encoding, so each `file.chunk` message stays well under typical socket
buffer sizes. `file.start.totalChunks` lets the receiver show progress and
detect truncated transfers.
