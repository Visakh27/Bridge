package com.bridge.app

import com.bridge.app.network.*
import com.bridge.app.notifications.*
import com.bridge.app.protocol.*
import java.nio.file.Files
import kotlinx.coroutines.*
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phase C on the phone: what is mirrored, and what the PC may do with it. */
class NotificationActionsTest {
    private class FakeSource : NotificationSource {
        val showing = linkedMapOf<String, PhoneNotification>()
        val cancelled = mutableListOf<String>()
        override fun find(key: String) = showing[key]
        override fun active() = showing.values.toList()
        override fun cancel(key: String): Boolean { cancelled += key; showing.remove(key); return true }
    }

    private class Pressed { val calls = mutableListOf<Pair<String, String?>>() }

    private fun note(key: String = "k1", app: String = "com.chat", title: String? = "Ana", text: String = "Lunch?",
                     pressed: Pressed? = null, actions: List<Pair<String, Boolean>> = listOf("Reply" to true, "Mark as read" to false),
                     clearable: Boolean = true, ongoing: Boolean = false, secret: Boolean = false, summary: Boolean = false,
                     result: Boolean = true) =
        PhoneNotification(key, app, "Chat", title, text, ongoing, summary, secret, clearable,
            actions.map { (t, r) -> PhoneAction(t, r) { reply -> pressed?.calls?.add(t to reply); result } })

    private fun mirror(source: FakeSource = FakeSource(), store: NotificationFilterStore = InMemoryNotificationFilterStore()) =
        NotificationMirror(NotificationFilter(store), source, "com.bridge.app")

    private suspend fun NotificationMirror.ask(message: BridgeMessage): List<BridgeMessage> {
        val replies = mutableListOf<BridgeMessage>()
        handleIncoming(message) { replies += it }
        return replies
    }

    private fun action(key: String, id: String, text: String? = null, request: String = "r1") =
        BridgeMessage.create(MessageType.NOTIFICATION_ACTION, NotificationActionPayload(request, key, id, text))

    private fun status(replies: List<BridgeMessage>) = replies.single().payloadAs<NotificationActionResultPayload>().status

    @Test
    fun only_eligible_notifications_are_mirrored() {
        val m = mirror()
        assertNotNull(m.onPosted(note(), withActions = true))
        assertNull(m.onPosted(note(key = "own", app = "com.bridge.app"), true))
        assertNull(m.onPosted(note(key = "o", ongoing = true), true))
        assertNull(m.onPosted(note(key = "s", secret = true), true))
        assertNull(m.onPosted(note(key = "g", summary = true), true))
        assertNull(m.onPosted(note(key = "t", title = null), true))
        assertNull(m.onPosted(note(key = "b", title = "  "), true))
        m.filter.setAllowed("com.bank", false)
        assertNull(m.onPosted(note(key = "bank", app = "com.bank"), true))
        assertTrue("blocked apps are still listed for the settings screen", "com.bank" in m.filter.snapshot.seen)
    }

    @Test
    fun post_carries_buttons_only_for_a_pc_that_supports_actions() {
        val m = mirror()
        val plain = m.onPosted(note(), withActions = false)!!
        assertNull(plain.actions); assertNull(plain.appId); assertNull(plain.canDismiss)
        val json = BridgeMessage.create(MessageType.NOTIFICATION_POST, plain).toJson()
        assertFalse("old PCs see the old wire format: $json", json.contains("actions") || json.contains("appId") || json.contains("null"))

        val rich = m.onPosted(note(text = "Lunch at 1?"), withActions = true)!!
        assertEquals("com.chat", rich.appId)
        assertEquals(true, rich.canDismiss)
        assertEquals(listOf("Reply", "Mark as read"), rich.actions!!.map { it.title })
        assertEquals(listOf(true, null), rich.actions!!.map { it.reply })
        assertTrue(rich.actions!!.all { Regex("^[0-9]+-[0-9a-f]{8}$").matches(it.id) })
    }

    @Test
    fun at_most_three_buttons_and_long_text_is_clipped_safely() {
        val m = mirror()
        val emoji = "😀".repeat(NotificationMirror.MAX_TITLE) // surrogate pairs
        val p = m.onPosted(note(title = emoji, text = "x".repeat(5000),
            actions = listOf("" to false, "A" to false, "B" to true, "C" to false, "D" to false)), true)!!
        assertEquals(listOf("A", "B", "C"), p.actions!!.map { it.title })
        assertTrue(p.title.length <= NotificationMirror.MAX_TITLE + 1)
        assertFalse("no half emoji", Character.isHighSurrogate(p.title.dropLast(1).last()))
        assertEquals(NotificationMirror.MAX_TEXT + 1, p.text.length)
    }

    @Test
    fun identical_updates_are_not_resent_but_changes_are() {
        val m = mirror()
        assertNotNull(m.onPosted(note(), true))
        assertNull(m.onPosted(note(), true))
        assertNotNull(m.onPosted(note(text = "Lunch at 1?"), true))
        // A post that didn't reach the PC is sent again next time.
        m.forget("k1")
        assertNotNull(m.onPosted(note(text = "Lunch at 1?"), true))
    }

    @Test
    fun a_toast_whose_notification_stops_qualifying_is_cleared_on_the_pc() {
        val m = mirror()
        assertEquals(MessageType.NOTIFICATION_POST, m.update(note(), true)!!.type)
        assertNull("unchanged", m.update(note(), true))
        val ongoing = m.update(note(ongoing = true), true)!!
        assertEquals(MessageType.NOTIFICATION_DISMISS, ongoing.type)
        assertNull("already cleared", m.update(note(ongoing = true), true))
        assertNull("never shown", m.update(note(key = "x", secret = true), true))
        assertEquals(MessageType.NOTIFICATION_POST, m.update(note(), true)!!.type)
        m.filter.setAllowed("com.chat", false)
        assertEquals(MessageType.NOTIFICATION_DISMISS, m.update(note(text = "new"), true)!!.type)
    }

    @Test
    fun removals_reach_the_pc_even_after_bridge_restarted() {
        val m = mirror()
        m.onPosted(note(), true)
        assertNotNull(m.onRemoved(note()))
        // A fresh mirror (Bridge restarted) doesn't remember k2, but the PC may still show it.
        val fresh = mirror()
        assertNotNull(fresh.onRemoved(note(key = "k2")))
        assertNull("never mirrored", fresh.onRemoved(note(key = "k3", ongoing = true)))
        fresh.filter.setAllowed("com.chat", false)
        assertNull("turned-off app", fresh.onRemoved(note(key = "k4")))
    }

    @Test
    fun concurrent_posts_and_removals_are_safe() {
        val m = mirror()
        val errors = java.util.concurrent.ConcurrentLinkedQueue<Throwable>()
        val threads = (0 until 8).map { t ->
            Thread {
                try {
                    repeat(2000) { i ->
                        val n = note(key = "k${i % 50}", text = "t$t-$i", app = "com.app${i % 7}")
                        m.update(n, true); if (i % 3 == 0) m.onRemoved(n)
                        if (i % 97 == 0) m.setAppAllowed("com.app${i % 7}", i % 2 == 0)
                    }
                } catch (e: Throwable) { errors += e }
            }
        }
        threads.forEach { it.start() }; threads.forEach { it.join() }
        assertTrue(errors.toString(), errors.isEmpty())
    }

    @Test
    fun reply_reaches_the_app_with_the_typed_text() = runBlocking {
        val src = FakeSource(); val pressed = Pressed()
        val m = mirror(src)
        src.showing["k1"] = note(pressed = pressed)
        val reply = m.onPosted(src.showing["k1"]!!, true)!!.actions!!.first { it.reply == true }
        assertEquals(NotificationActionStatus.OK, status(m.ask(action("k1", reply.id, "On my way"))))
        assertEquals(listOf("Reply" to "On my way"), pressed.calls)
        val readId = NotificationMirror.actionId(1, src.showing["k1"]!!.actions[1])
        assertEquals(NotificationActionStatus.OK, status(m.ask(action("k1", readId))))
        assertEquals("Mark as read" to null, pressed.calls.last())
    }

    @Test
    fun malformed_requests_are_invalid_and_press_nothing() = runBlocking {
        val src = FakeSource(); val pressed = Pressed()
        val m = mirror(src)
        val n = note(pressed = pressed).also { src.showing["k1"] = it }
        val replyId = NotificationMirror.actionId(0, n.actions[0])
        val plainId = NotificationMirror.actionId(1, n.actions[1])
        for (text in listOf(null, "", "   ", "x".repeat(NotificationMirror.MAX_REPLY + 1)))
            assertEquals("reply '$text'", NotificationActionStatus.INVALID, status(m.ask(action("k1", replyId, text))))
        assertEquals(NotificationActionStatus.INVALID, status(m.ask(action("k1", plainId, "text for a plain button"))))
        assertTrue(pressed.calls.isEmpty())
        assertEquals(NotificationActionStatus.OK, status(m.ask(action("k1", replyId, "x".repeat(NotificationMirror.MAX_REPLY)))))
    }

    @Test
    fun stale_or_unknown_targets_are_gone_and_changed_buttons_are_not_pressed() = runBlocking {
        val src = FakeSource(); val pressed = Pressed()
        val m = mirror(src)
        val before = note(pressed = pressed, actions = listOf("Archive" to false, "Delete" to false)).also { src.showing["k1"] = it }
        val archiveId = NotificationMirror.actionId(0, before.actions[0])
        assertEquals(NotificationActionStatus.GONE, status(m.ask(action("missing", archiveId))))
        assertEquals(NotificationActionStatus.GONE, status(m.ask(action("k1", "9-00000000"))))
        // The app swapped its buttons: index 0 is now "Delete". The old "Archive" id must not press it.
        src.showing["k1"] = note(pressed = pressed, actions = listOf("Delete" to false))
        assertEquals(NotificationActionStatus.GONE, status(m.ask(action("k1", archiveId))))
        assertTrue(pressed.calls.isEmpty())
    }

    @Test
    fun blocked_apps_and_hidden_notifications_are_refused() = runBlocking {
        val src = FakeSource(); val pressed = Pressed()
        val m = mirror(src)
        val n = note(pressed = pressed).also { src.showing["k1"] = it }
        val id = NotificationMirror.actionId(1, n.actions[1])
        m.filter.setAllowed("com.chat", false)
        assertEquals(NotificationActionStatus.REFUSED, status(m.ask(action("k1", id))))
        m.filter.setAllowed("com.chat", true)
        src.showing["k1"] = note(pressed = pressed, secret = true)
        assertEquals(NotificationActionStatus.REFUSED, status(m.ask(action("k1", id))))
        src.showing["k1"] = note(key = "k1", app = "com.bridge.app", pressed = pressed)
        assertEquals(NotificationActionStatus.REFUSED, status(m.ask(action("k1", id))))
        assertTrue(pressed.calls.isEmpty())
    }

    @Test
    fun app_refusing_the_action_is_reported_as_failed() = runBlocking {
        val src = FakeSource()
        val m = mirror(src)
        val n = note(result = false).also { src.showing["k1"] = it }
        assertEquals(NotificationActionStatus.FAILED, status(m.ask(action("k1", NotificationMirror.actionId(1, n.actions[1])))))
        src.showing["k2"] = PhoneNotification("k2", "com.chat", "Chat", "T", "", actions = listOf(PhoneAction("Boom", false) { throw IllegalStateException("cancelled") }))
        assertEquals(NotificationActionStatus.FAILED, status(m.ask(action("k2", NotificationMirror.actionId(0, src.showing["k2"]!!.actions[0])))))
    }

    @Test
    fun dismiss_from_the_pc_clears_only_what_bridge_mirrors() = runBlocking {
        val src = FakeSource()
        val m = mirror(src)
        fun dismiss(key: String) = BridgeMessage.create(MessageType.NOTIFICATION_DISMISS, NotificationDismissPayload(key))
        src.showing["a"] = note(key = "a")
        src.showing["pinned"] = note(key = "pinned", clearable = false)
        src.showing["bank"] = note(key = "bank", app = "com.bank")
        m.filter.setAllowed("com.bank", false)
        assertTrue(m.ask(dismiss("a")).isEmpty()) // the listener's onNotificationRemoved tells the PC
        assertTrue(m.ask(dismiss("pinned")).isEmpty())
        assertTrue(m.ask(dismiss("bank")).isEmpty())
        assertEquals(listOf("a"), src.cancelled)
        // Already gone on the phone: tell the PC so its toast goes too.
        val back = m.ask(dismiss("gone"))
        assertEquals(MessageType.NOTIFICATION_DISMISS, back.single().type)
        assertEquals("gone", back.single().payloadAs<NotificationDismissPayload>().key)
    }

    @Test
    fun mute_from_the_pc_blocks_the_app_and_clears_its_toasts() = runBlocking {
        val src = FakeSource()
        val m = mirror(src)
        for (k in listOf("c1", "c2")) { src.showing[k] = note(key = k); m.onPosted(src.showing[k]!!, true) }
        src.showing["c3"] = note(key = "c3") // showing but never sent to the PC
        src.showing["o1"] = note(key = "o1", app = "com.other"); m.onPosted(src.showing["o1"]!!, true)
        val replies = m.ask(BridgeMessage.create(MessageType.NOTIFICATION_MUTE, NotificationMutePayload("com.chat")))
        assertEquals(setOf("c1", "c2"), replies.map { it.payloadAs<NotificationDismissPayload>().key }.toSet())
        assertFalse(m.filter.isAllowed("com.chat"))
        assertTrue(m.filter.isAllowed("com.other"))
        assertNull(m.onPosted(note(key = "c4"), true))
        // Bridge itself and junk ids are ignored.
        assertTrue(m.ask(BridgeMessage.create(MessageType.NOTIFICATION_MUTE, NotificationMutePayload("com.bridge.app"))).isEmpty())
        assertTrue(m.ask(BridgeMessage.create(MessageType.NOTIFICATION_MUTE, NotificationMutePayload("x".repeat(300)))).isEmpty())
        assertEquals(setOf("com.chat"), m.filter.snapshot.decisions.filterValues { !it }.keys)
    }

    @Test
    fun filter_choices_persist_and_new_apps_follow_the_default() {
        val store = InMemoryNotificationFilterStore()
        val f = NotificationFilter(store)
        f.setAllowNewApps(false)
        f.setAllowed("com.chat", true)
        val reopened = NotificationFilter(store)
        assertTrue(reopened.isAllowed("com.chat"))
        assertFalse(reopened.isAllowed("com.unknown"))
        repeat(NotificationFilter.MAX_SEEN + 50) { reopened.noteSeen("app$it", "App $it") }
        assertEquals(NotificationFilter.MAX_SEEN, reopened.snapshot.seen.size)
        assertTrue("most recent kept", "app${NotificationFilter.MAX_SEEN + 49}" in reopened.snapshot.seen)
        val json = BridgeJson.encodeToString(NotificationFilter.Saved.serializer(), reopened.snapshot)
        assertEquals(reopened.snapshot, BridgeJson.decodeFromString(NotificationFilter.Saved.serializer(), json))
    }

    @Test
    fun broken_storage_never_breaks_mirroring() {
        val f = NotificationFilter(object : NotificationFilterStore {
            override fun load(): NotificationFilter.Saved = throw IllegalStateException("corrupt")
            override fun save(saved: NotificationFilter.Saved) = throw IllegalStateException("disk full")
        })
        assertTrue(f.isAllowed("com.chat"))
        f.setAllowed("com.chat", false)
        assertFalse(f.isAllowed("com.chat"))
    }

    private object NoClipboard : ClipboardAccess {
        override fun readText(): String? = null
        override fun writeText(text: String) {}
        override fun setChangeListener(listener: (String) -> Unit) {}
    }

    @Test
    fun session_advertises_actions_and_answers_the_pc() = runBlocking {
        val src = FakeSource(); val pressed = Pressed()
        val m = mirror(src)
        val n = note(pressed = pressed).also { src.showing["k1"] = it }
        val replyId = NotificationMirror.actionId(0, n.actions[0])
        val got = CompletableDeferred<Pair<List<String>, String>>()
        TestPc {
            handshake()
            val features = receive().payloadAs<CapabilitiesPayload>().features
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.NOTIFICATIONS, Features.NOTIFICATIONS_ACTIONS))))
            send(action("k1", replyId, "Sure", request = "abc"))
            while (true) {
                val m2 = receive()
                if (m2.type == MessageType.NOTIFICATION_ACTION_RESULT) {
                    val r = m2.payloadAs<NotificationActionResultPayload>()
                    got.complete(features to "${r.requestId}:${r.status}"); break
                }
            }
            ignoreRest()
        }.use { pc ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                FileTransferClient {}, ClipboardSyncClient(NoClipboard, this), Files.createTempDirectory("dl").toFile(), notifications = m)
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            val (features, result) = withTimeout(10_000) { got.await() }
            assertTrue(Features.NOTIFICATIONS_ACTIONS in features)
            assertEquals("abc:ok", result)
            assertEquals(listOf("Reply" to "Sure"), pressed.calls)
            assertTrue(s.peerSupports(Features.NOTIFICATIONS_ACTIONS))
            s.close(); job.cancelAndJoin()
            assertFalse(s.peerSupports(Features.NOTIFICATIONS_ACTIONS))
        }
    }

    @Test
    fun session_without_a_handler_does_not_offer_actions_and_ignores_requests() = runBlocking {
        val features = CompletableDeferred<List<String>>()
        val stillAlive = CompletableDeferred<Boolean>()
        TestPc {
            handshake()
            features.complete(receive().payloadAs<CapabilitiesPayload>().features)
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.NOTIFICATIONS_ACTIONS))))
            send(action("k1", "0-00000000"))
            send(BridgeMessage.createEmpty(MessageType.PRESENCE_PING))
            while (true) { val r = receive(); if (r.type == MessageType.PRESENCE_PONG) { stillAlive.complete(true); break }; check(r.type != MessageType.NOTIFICATION_ACTION_RESULT) }
            ignoreRest()
        }.use { pc ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                FileTransferClient {}, ClipboardSyncClient(NoClipboard, this), Files.createTempDirectory("dl").toFile())
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            assertFalse(Features.NOTIFICATIONS_ACTIONS in withTimeout(10_000) { features.await() })
            assertTrue(withTimeout(10_000) { stillAlive.await() })
            s.close(); job.cancelAndJoin()
        }
    }

    @Test
    fun malformed_requests_from_the_pc_never_break_the_session() = runBlocking {
        val src = FakeSource(); val pressed = Pressed()
        val m = mirror(src)
        val n = note(pressed = pressed).also { src.showing["k1"] = it }
        val readId = NotificationMirror.actionId(1, n.actions[1])
        val results = java.util.concurrent.ConcurrentLinkedQueue<String>()
        val done = CompletableDeferred<Unit>()
        TestPc {
            handshake(); receive()
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.NOTIFICATIONS, Features.NOTIFICATIONS_ACTIONS))))
            val junk = kotlinx.serialization.json.buildJsonObject {
                put("actionId", kotlinx.serialization.json.JsonPrimitive(5))
                put("key", kotlinx.serialization.json.JsonArray(emptyList()))
            }
            send(BridgeMessage(type = MessageType.NOTIFICATION_ACTION, payload = junk))
            send(BridgeMessage.createEmpty(MessageType.NOTIFICATION_ACTION))
            send(BridgeMessage.createEmpty(MessageType.NOTIFICATION_DISMISS))
            send(BridgeMessage.createEmpty(MessageType.NOTIFICATION_MUTE))
            send(BridgeMessage.create(MessageType.NOTIFICATION_MUTE, NotificationMutePayload("")))
            send(action("k1", readId, request = "real"))
            while (true) {
                val r = receive()
                if (r.type == MessageType.NOTIFICATION_ACTION_RESULT) {
                    val p = r.payloadAs<NotificationActionResultPayload>(); results += "${p.requestId}:${p.status}"
                    if (p.requestId == "real") { done.complete(Unit); break }
                }
            }
            ignoreRest()
        }.use { pc ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                FileTransferClient {}, ClipboardSyncClient(NoClipboard, this), Files.createTempDirectory("dl").toFile(), notifications = m)
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            withTimeout(10_000) { done.await() }
            assertEquals(listOf("real:ok"), results.toList())
            assertEquals(listOf("Mark as read" to null), pressed.calls)
            assertTrue("nothing muted", m.filter.snapshot.decisions.isEmpty())
            assertTrue(s.isConnected)
            s.close(); job.cancelAndJoin()
        }
    }
}
