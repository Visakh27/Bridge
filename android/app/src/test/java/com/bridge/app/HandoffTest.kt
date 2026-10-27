package com.bridge.app

import com.bridge.app.handoff.*
import com.bridge.app.network.*
import com.bridge.app.protocol.*
import java.nio.file.Files
import java.util.Collections
import kotlinx.coroutines.*
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phase G on the phone: links and text handed between phone and PC. */
class HandoffTest {
    private class Inbox(enabled: Boolean = true, var clock: Long = 1_000_000L) {
        @Volatile var on = enabled
        val links: MutableList<HandoffLink> = Collections.synchronizedList(mutableListOf())
        val texts: MutableList<String> = Collections.synchronizedList(mutableListOf())
        val log: MutableList<String> = Collections.synchronizedList(mutableListOf())
        val inbox = HandoffInbox({ on }, { links += it }, { texts += it }, { clock }).also {
            it.onCompleted = { d, k, s -> log += "$d $k $s" }
        }
        val replies: MutableList<BridgeMessage> = Collections.synchronizedList(mutableListOf())
        suspend fun hand(p: HandoffPayload): String {
            val before = replies.size
            inbox.handleIncoming(BridgeMessage.create(MessageType.HANDOFF_OPEN, p)) { replies += it }
            val ack = replies.drop(before).single().payloadAs<HandoffAckPayload>()
            assertEquals(p.id, ack.id)
            return ack.status
        }
    }

    private fun url(id: String, u: String, title: String? = null) = HandoffPayload(id, HandoffKind.URL, url = u, title = title)
    private fun text(id: String, t: String) = HandoffPayload(id, HandoffKind.TEXT, text = t)

    @Test
    fun accepts_plain_web_links() {
        for ((u, host) in listOf(
            "https://example.com/a?b=c#d" to "example.com",
            "http://EXAMPLE.com:8080/" to "example.com",
            "https://bücher.de/x" to "xn--bcher-kva.de",
            "HTTPS://BÜCHER.de:8443/ä?q=ö#f" to "xn--bcher-kva.de",
            "https://192.168.1.10/" to "192.168.1.10",
            "https://[2001:db8::1]:443/" to "[2001:db8::1]",
        )) {
            val link = HandoffRules.checkUrl(u)
            assertNotNull(u, link)
            assertEquals(u, host, link!!.host)
            assertTrue(u, link.url.startsWith("http"))
            // What's sent and opened carries the host that's shown, in ASCII.
            assertTrue(link.url, java.net.URI(link.url).host.lowercase() == host && link.url.startsWith(link.url.substringBefore("://") + "://" + host))
            assertEquals(link, HandoffRules.checkUrl(link.url)) // stable
        }
        assertEquals("https://xn--bcher-kva.de:8443/ä?q=ö#f", HandoffRules.checkUrl("HTTPS://BÜCHER.de:8443/ä?q=ö#f")!!.url)
        assertNull(HandoffRules.checkUrl("https://bücher.de:80x/"))
    }

    @Test
    fun refuses_anything_but_plain_web_links() {
        for (bad in listOf("javascript:alert(1)", "file:///sdcard/x.apk", "content://com.android.contacts/", "intent://scan/#Intent;scheme=zxing;end",
            "ftp://example.com/", "https://user:pw@example.com/", "https://example.com@evil.com/", "https://exa mple.com/", "https://example.com/\nx",
            "https:///nohost", "example.com", "", null, "https://example.com/" + "a".repeat(HandoffRules.MAX_URL)))
            assertNull("$bad", HandoffRules.checkUrl(bad))
    }

    @Test
    fun titles_and_text_are_cleaned_and_limited() {
        val link = HandoffRules.checkUrl("https://example.com/", "Pay‮gnp.exe\u0007 here " + "x".repeat(400))!!
        assertFalse(link.title!!.contains('‮'))
        assertFalse(link.title!!.contains('\u0007'))
        assertTrue(link.title!!.length <= HandoffRules.MAX_TITLE)
        assertNull(HandoffRules.checkUrl("https://example.com/", "   ")!!.title)
        assertNull(HandoffRules.checkText("   "))
        assertNull(HandoffRules.checkText("a".repeat(HandoffRules.MAX_TEXT + 1)))
        assertEquals("hello\nworld", HandoffRules.checkText("hello\nworld"))
        assertFalse(HandoffRules.preview("line one\nline two").contains('\n'))
    }

    @Test
    fun links_are_offered_never_opened_and_text_is_copied() = runBlocking {
        val t = Inbox()
        assertEquals(HandoffStatus.SHOWN, t.hand(url("a1", "https://example.com/page", "Example")))
        assertEquals(HandoffStatus.COPIED, t.hand(text("a2", "some text")))
        assertEquals(HandoffStatus.INVALID, t.hand(url("a3", "intent://x#Intent;end")))
        assertEquals(HandoffStatus.INVALID, t.hand(HandoffPayload("a4", "app", url = "https://example.com")))
        assertEquals(HandoffStatus.INVALID, t.hand(text("a5", "")))
        // A retry of one already handled: same answer, nothing shown twice.
        assertEquals(HandoffStatus.SHOWN, t.hand(url("a1", "https://example.com/page", "Example")))
        assertEquals(listOf("https://example.com/page"), t.links.map { it.url })
        assertEquals("Example", t.links.single().title)
        assertEquals(listOf("some text"), t.texts)
    }

    @Test
    fun copies_of_one_id_arriving_together_act_once() = runBlocking {
        val t = Inbox()
        val answers = Collections.synchronizedList(mutableListOf<String>())
        coroutineScope {
            repeat(16) {
                launch(Dispatchers.Default) {
                    t.inbox.handleIncoming(BridgeMessage.create(MessageType.HANDOFF_OPEN, url("same", "https://example.com/once"))) { r ->
                        answers += r.payloadAs<HandoffAckPayload>().status
                    }
                }
            }
        }
        assertEquals(List(16) { HandoffStatus.SHOWN }, answers.toList())
        assertEquals(1, t.links.size)
    }

    @Test
    fun switched_off_refuses_everything() = runBlocking {
        val t = Inbox(enabled = false)
        assertEquals(HandoffStatus.DISABLED, t.hand(url("b1", "https://example.com/")))
        assertEquals(HandoffStatus.DISABLED, t.hand(text("b2", "x")))
        assertTrue(t.links.isEmpty() && t.texts.isEmpty())
    }

    @Test
    fun a_flood_is_refused_until_the_minute_passes() = runBlocking {
        val t = Inbox()
        repeat(HandoffInbox.MAX_PER_MINUTE) { assertEquals(HandoffStatus.COPIED, t.hand(text("c$it", "t$it"))) }
        assertEquals(HandoffStatus.BUSY, t.hand(text("c-over", "x")))
        assertEquals(HandoffInbox.MAX_PER_MINUTE, t.texts.size)
        t.clock += 61_000
        assertEquals(HandoffStatus.COPIED, t.hand(text("c-later", "x")))
    }

    @Test
    fun garbage_gets_no_answer_and_breaks_nothing() = runBlocking {
        val t = Inbox()
        t.inbox.handleIncoming(BridgeMessage.create(MessageType.HANDOFF_OPEN, HandoffPayload("", HandoffKind.TEXT, text = "x"))) { t.replies += it }
        t.inbox.handleIncoming(BridgeMessage.create(MessageType.HANDOFF_OPEN, HandoffPayload("x".repeat(65), HandoffKind.TEXT, text = "x"))) { t.replies += it }
        t.inbox.handleIncoming(BridgeMessage.create(MessageType.HANDOFF_ACK, HandoffAckPayload("nobody-asked", HandoffStatus.SHOWN))) { t.replies += it }
        assertTrue(t.replies.isEmpty())
        assertEquals(HandoffStatus.COPIED, t.hand(text("d1", "still fine")))
    }

    @Test
    fun sending_waits_for_the_answer_and_normalizes_it() = runBlocking {
        val t = Inbox()
        val sent = Collections.synchronizedList(mutableListOf<HandoffPayload>())
        suspend fun answerWith(status: String) {
            withTimeout(5_000) { while (sent.isEmpty()) delay(5) }
            val p = sent.removeAt(0)
            t.inbox.handleIncoming(BridgeMessage.create(MessageType.HANDOFF_ACK, HandoffAckPayload(p.id, status))) {}
        }
        val send: suspend (BridgeMessage) -> Boolean = { m -> sent += m.payloadAs<HandoffPayload>(); true }

        val a = async { t.inbox.sendShared("  https://example.com/article ", "An article", send) }
        withTimeout(5_000) { while (sent.isEmpty()) delay(5) }
        assertEquals(HandoffKind.URL, sent[0].kind)
        assertEquals("https://example.com/article", sent[0].url)
        assertEquals("An article", sent[0].title)
        answerWith(HandoffStatus.SHOWN)
        assertEquals(HandoffStatus.SHOWN, a.await())

        val b = async { t.inbox.sendShared("read this: https://example.com/x", null, send) }
        withTimeout(5_000) { while (sent.isEmpty()) delay(5) }
        assertEquals(HandoffKind.TEXT, sent[0].kind)
        assertEquals("read this: https://example.com/x", sent[0].text)
        answerWith("SECRET-made-up-status")
        assertEquals(HandoffStatus.INVALID, b.await())

        assertEquals(HandoffStatus.INVALID, t.inbox.sendUrl("javascript:alert(1)", null, send))
        assertEquals(HandoffStatus.INVALID, t.inbox.sendShared(null, null, send))
        assertEquals(HandoffStatus.UNAVAILABLE, t.inbox.sendText("x") { false })
        assertTrue(t.log.none { "SECRET" in it || "example.com" in it || "read this" in it })
        assertTrue(t.log.contains("out url shown"))
    }

    @Test
    fun no_answer_is_unavailable_not_a_hang() = runBlocking {
        val t = Inbox()
        val started = System.nanoTime()
        assertEquals(HandoffStatus.UNAVAILABLE, t.inbox.sendText("x") { true })
        assertTrue((System.nanoTime() - started) / 1_000_000 >= HandoffInbox.ACK_TIMEOUT_MS - 100)
    }

    // ------------------------------------------------------------------ over a real session

    private object NoClipboard : ClipboardAccess {
        override fun readText(): String? = null
        override fun writeText(text: String) {}
        override fun setChangeListener(listener: (String) -> Unit) {}
    }

    @Test
    fun session_offers_handoff_and_answers_the_pc() = runBlocking {
        val t = Inbox()
        val result = CompletableDeferred<String>()
        TestPc {
            handshake()
            val features = receive().payloadAs<CapabilitiesPayload>().features
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES, Features.HANDOFF))))
            send(BridgeMessage.create(MessageType.HANDOFF_OPEN, url("s1", "https://example.com/from-pc", "From the PC")))
            send(BridgeMessage.create(MessageType.HANDOFF_OPEN, url("s2", "file:///etc/passwd")))
            send(BridgeMessage.create(MessageType.HANDOFF_OPEN, text("s3", "clipboard text")))
            val acks = mutableListOf<String>()
            while (acks.size < 3) {
                val m = receive()
                if (m.type == MessageType.HANDOFF_ACK) m.payloadAs<HandoffAckPayload>().let { acks += "${it.id}=${it.status}" }
            }
            result.complete("${Features.HANDOFF in features}|${acks.joinToString(",")}")
            ignoreRest()
        }.use { pc ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                FileTransferClient {}, ClipboardSyncClient(NoClipboard, this), Files.createTempDirectory("dl").toFile(), handoff = t.inbox)
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            assertEquals("true|s1=shown,s2=invalid,s3=copied", withTimeout(15_000) { result.await() })
            assertEquals(listOf("https://example.com/from-pc"), t.links.map { it.url })
            assertEquals(listOf("clipboard text"), t.texts)
            s.close(); job.cancelAndJoin()
        }
    }

    @Test
    fun without_a_handler_the_phone_does_not_offer_handoff() = runBlocking {
        val features = CompletableDeferred<List<String>>()
        TestPc {
            handshake(); features.complete(receive().payloadAs<CapabilitiesPayload>().features)
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES))))
            ignoreRest()
        }.use { pc ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                FileTransferClient {}, ClipboardSyncClient(NoClipboard, this), Files.createTempDirectory("dl").toFile())
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            assertFalse(Features.HANDOFF in withTimeout(10_000) { features.await() })
            s.close(); job.cancelAndJoin()
        }
    }
}
