# Bridge Security Model

1. **Device identity** — each install generates a long-term NIST P-256
   signing key on first run (`DeviceIdentity.LoadOrCreate` /
   `DeviceIdentity.loadOrCreate`). The private key never leaves the device
   (Windows: DPAPI-protected file; Android: non-exportable Android Keystore
   key, which works on every supported API level from 26 up).
2. **Pairing** — Windows shows a QR code containing a single-use pairing token
   that expires after 5 minutes, the PC's address, and the PC's identity public
   key. The phone connects and, during the handshake, checks that the PC
   signed the transcript with exactly that key; only then does it send the
   token and its own identity key. The PC accepts only if the token matches
   *and* the phone's handshake signature verifies under the key it presented
   (proof the phone owns it). The pairing (peer id, name, public key) is then
   stored on both sides. A machine on the same Wi-Fi that answers in the PC's
   place can't sign as the PC, so it never sees the token.
3. **Authenticated sessions** — every connection runs an ephemeral ECDH
   (P-256) handshake in which both sides sign the transcript (both device ids
   and both ephemeral keys) with their identity keys. Reconnecting phones are
   accepted only if their signature verifies under the key stored at pairing,
   so knowing a paired device's id is not enough to connect.
4. **Session encryption** — HKDF-SHA256 (salted with the transcript hash)
   derives one AES-256-GCM key per direction. Nonces are per-direction
   counters; a frame with an unexpected counter (replayed, dropped or
   reordered) or a bad tag ends the connection. Tags are verified before any
   payload is parsed.
5. **File integrity and safety** — every transfer's SHA-256 is sent in
   `file.start` and verified against the reassembled file before `file.ack`
   reports success. Incoming file names are reduced to a safe leaf name
   (no `..`, separators, drive letters, control or invisible format
   characters such as the right-to-left override, or reserved Windows names),
   shortened to fit the filesystem, and never overwrite an existing file. Folder
   paths in folder transfers get the same treatment per segment (no `..`,
   at most 16 levels, always inside the download folder).
   Partial files use a random local temp name; non-resumable ones are deleted
   on failure or disconnect.
   **Resumable partials** are kept for 24 h in a hidden `.bridge-partial`
   folder, keyed by the sending device, so another paired device can't pick up
   or append to them; at most 32 per device. The whole-file SHA-256 check at
   the end still covers chunks received on earlier connections, and a re-offer
   is answered "already received" only for the same transfer id *and* hash.
6. **Clipboard** — never written to disk or logs. Sync is opt-in (off by
   default), text-only, capped at 10,000 characters, skips content that looks
   like keys or passwords, and suppresses echo loops.
7. **Revocation** — a paired device can be forgotten on either side ("Forget
   this phone" on Windows, "Forget this PC" on Android). Windows deletes the
   stored peer key, drops any live session and answers future connections
   from that identity with `DEVICE_UNPAIRED`; the phone then deletes its
   pairing and asks the user to pair again.
8. **Discovery privacy** — PCs never announce themselves. A phone's probe
   names its PC only through a tag derived from that PC's public key and a
   fresh nonce, so other devices on the Wi-Fi can't identify or track Bridge
   PCs. Replies are signed, so a forged reply can't point the phone at
   another machine, and the PC limits how often it replies.
9. **Resource limits** — before authentication a peer can only make the
   other side buffer 4 KiB (handshake) or 64 KiB (first encrypted message on
   the PC); handshakes time out after 15 s. Failures inside one message
   handler (a locked Windows clipboard, a disk error) are contained to that
   message and never drop the connection.
10. **Logging** — diagnostic logs record connection/transfer *metadata*
   (timestamps, byte counts, error codes) and never message payload contents.

## Known limits (MVP)

- Discovery only works where the network passes UDP broadcast between
  devices; networks with client isolation (some guest/hotel Wi-Fi) block it,
  and the phone can then only reach the PC at its last known address.
- Notification contents are sent to the PC in full once the user grants
  notification access; per-app filtering is follow-up work.
