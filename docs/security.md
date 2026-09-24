# Bridge Security Model

1. **Device identity** — each install generates a long-term X25519 keypair
   (`CryptoService.GenerateIdentity` / `CryptoService.generateIdentity`) on
   first run. The private key never leaves the device (Windows: DPAPI-protected
   file; Android: Android Keystore-backed).
2. **Pairing** — Windows shows a QR code containing a short-lived, single-use
   pairing token + its public key. Android scans it, both sides exchange
   long-term public keys over the just-established encrypted channel, and the
   pairing (peer public key + device name) is persisted locally.
3. **Session encryption** — every connection (paired or not-yet-paired,
   pairing itself runs inside an ephemeral-key encrypted channel too) derives
   a fresh AES-256-GCM session key pair (one per direction) via ECDH + HKDF.
   Nonces are per-message random 96-bit values; GCM tags are verified before
   any payload is parsed.
4. **File integrity** — every transfer's SHA-256 is sent in `file.start` and
   verified against the reassembled file before `file.ack` reports success.
5. **Clipboard** — never written to disk or logs. Sync is opt-in and can be
   restricted to text-only or disabled outright per direction.
6. **Revocation** — a paired device can be revoked from Settings on either
   side, which deletes the stored peer public key and refuses future
   handshakes from that identity until re-paired.
7. **Logging** — diagnostic logs record connection/transfer *metadata*
   (timestamps, byte counts, error codes) and never message payload contents.
