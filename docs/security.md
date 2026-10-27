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
10. **Notification actions** — the paired PC can do with a mirrored
   notification what the user could do from the phone's notification shade,
   and nothing more: reply, press the app's own buttons, dismiss, or stop that
   app being mirrored. It can't open apps on the phone (buttons that would are
   not offered), press buttons the app marked as needing the phone unlocked,
   or act on notifications Bridge doesn't mirror (apps turned off on the phone,
   ongoing or secret ones); every request is checked against the notification
   as it is at that moment. Button ids depend on the button's title, so a stale
   id from the PC can't press a different button the app put in its place.
   Only the phone can turn a muted app back on. On the PC, notification text is
   treated as untrusted (control and bidi-override characters removed, lengths
   capped), and toast arguments are encoded so no notification content can
   change what a click does.
11. **Diagnostics log** — metadata only, enforced by construction: events
   take typed fields (numbers, flags, short code words such as a status or an
   exception type name, and ids as a salted 8-character hash that changes every
   run), so file names, folder names, clipboard text, notification text,
   device names, addresses, keys and tokens can't be written. Kept on the
   device (Windows: `%LOCALAPPDATA%\Bridge\logs`; Android: app-private
   storage), at most 3 × 256 KB, deleted after 14 days, and only leaves the
   device if the user saves or shares a report. Turning diagnostics off in
   Settings deletes the log. Tests check real sessions on both platforms for
   leaks.
12. **Photos** — off until the user turns on "Let your PC browse photos" on
   the phone; then the paired PC can list photos, get small previews and
   fetch originals, nothing else. Requests name photos only by MediaStore id
   (digits), never by path, and go through Android's photo permission, so the
   PC sees exactly what Android lets Bridge see (on Android 14 "selected
   photos", just those). Turning it off answers every request `disabled` at
   once. Originals travel as normal encrypted, hash-checked transfers; the
   phone's temporary copy is deleted once delivered. On the PC, previews are
   accepted only as small JPEGs and photo names are cleaned like any received
   file name. Logs record request kinds and outcomes, never ids or names.
13. **File Explorer** — files leave the PC only when the user picks "Send to
   › Bridge" or "Share › Bridge" for them. Bridge accepts only absolute paths
   that exist (at most 1,000 per request) and sends them through the normal
   transfer. If the phone isn't connected, the request is held in memory (not
   on disk) for up to 30 minutes, with a notification that can cancel it; it
   is dropped if Bridge exits. The Send to entry is a per-user shortcut; the
   MSIX uses Windows' Share contract instead.
14. **Retry copies** — a file shared from another Android app is copied into
   Bridge's private cache to send it. If the send fails, the copy is kept (at
   most 24 h, or until "Clear" in Recent transfers) so it can be retried;
   a cancelled send's copy is deleted at once.
15. **Links and text (handoff)** — only a paired, connected peer can hand
   over a link or text, and each side can turn receiving off (on by default).
   Links must be plain `http`/`https` URLs (≤ 2048 chars) with a host and no
   user info; `javascript:`, `file:`, `intent:`, `ms-settings:` and every
   other scheme are refused on both the sending and receiving side, and
   toast-button arguments are checked again before a link is opened, since
   anything on the PC can activate Bridge with arguments. Both sides rebuild
   the link around its ASCII (punycode) host, show that host, and open exactly
   that. The phone never opens a link by itself: it shows a notification, and
   the link opens only through a browser-capable app when the user taps it.
   The PC also offers links in a notification unless the user turns on
   "Open links right away". Titles lose control and bidi-override
   characters. Text goes on the clipboard (up to 10,000 chars) without being
   synced back. A peer may hand over at most 20 a minute; repeats of the same
   request are answered without acting twice. Logs record direction, kind
   and outcome only, never the link or text.
16. **Several devices** — each phone is paired, authenticated and revoked on
   its own; forgetting one phone (on the PC) or one PC (on the phone) never
   touches the others. A PC accepts at most 8 phones at once. Phones connected
   to the same PC may belong to different people, so nothing from one phone
   is passed to another: one phone's clipboard goes only to the PC (not
   relayed to the other phones), answers to photo, notification and handoff
   requests count only from the phone that was asked, a reply typed in a
   toast goes only to the phone the notification came from, and toasts,
   previews and handoff limits are kept per phone. The PC's own clipboard does
   go to every connected phone with clipboard sync on; the Settings panel says
   so. Logs record how many phones are connected and hashed ids, never names.

## Known limits (MVP)

- A phone connected to a fallback PC stays with it until that connection
  ends; it doesn't jump back to the chosen PC the moment that one comes into
  reach ("Use this PC" in Settings switches at once).

- A handed-over link is still a link to any web site: Bridge checks its form,
  not its destination. The notification shows the real host so the user can
  judge it before opening.

- Discovery only works where the network passes UDP broadcast between
  devices; networks with client isolation (some guest/hotel Wi-Fi) block it,
  and the phone can then only reach the PC at its last known address.
- Notification contents of the apps the user leaves on are sent to the PC in
  full once notification access is granted. New apps are mirrored by default;
  the phone's "Notifications on your PC" screen can turn that off.
