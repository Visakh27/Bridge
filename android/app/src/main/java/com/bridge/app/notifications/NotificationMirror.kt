package com.bridge.app.notifications

import com.bridge.app.protocol.*
import java.security.MessageDigest
import kotlinx.serialization.Serializable

/**
 * A notification as Bridge sees it — a plain snapshot of an Android
 * StatusBarNotification, so the mirroring rules can be tested on the JVM.
 * [actions] are the notification's buttons in order; each knows how to press
 * itself (BridgeNotificationListener fills [PhoneAction.perform] with the
 * PendingIntent / RemoteInput call).
 */
data class PhoneNotification(
    val key: String,
    val appId: String,
    val appName: String,
    val title: String?,
    val text: String,
    val ongoing: Boolean = false,
    val groupSummary: Boolean = false,
    val secret: Boolean = false,
    val clearable: Boolean = true,
    val actions: List<PhoneAction> = emptyList(),
)

class PhoneAction(
    val title: String,
    /** Takes free-form text (RemoteInput). */
    val isReply: Boolean,
    /** Presses the button (with the reply text for reply actions); false or an exception = the app refused. */
    val perform: (replyText: String?) -> Boolean,
)

/** What the phone can do with live notifications (BridgeNotificationListener on a device). */
interface NotificationSource {
    /** The notification currently showing under [key], or null if it's gone. */
    fun find(key: String): PhoneNotification?
    fun active(): List<PhoneNotification>
    /** Clears it from the phone; false if it couldn't be cleared. */
    fun cancel(key: String): Boolean
}

/** Handles PC → phone notification messages on behalf of BridgeClientSession. */
interface NotificationCommandHandler {
    suspend fun handleIncoming(message: BridgeMessage, reply: suspend (BridgeMessage) -> Unit)
}

/**
 * The phone side of notification mirroring (blueprint section 11) and of
 * notification actions: decides what reaches the PC (per-app [filter], no
 * ongoing / summary / secret notifications, no Bridge's own), builds the
 * payloads, skips repeats that change nothing, and carries out what the PC
 * asks — reply, press a button, dismiss, mute an app — only for notifications
 * that are still showing and that Bridge would mirror.
 *
 * Button ids are "<index>-<hash of title and kind>", so if the app replaces
 * the notification's buttons after the PC saw them, an old id matches nothing
 * (answered "gone") instead of pressing whatever button now sits at that index.
 */
class NotificationMirror(
    val filter: NotificationFilter,
    private val source: NotificationSource,
    private val ownAppId: String,
) : NotificationCommandHandler {

    companion object {
        const val MAX_ACTIONS = 3
        const val MAX_TITLE = 200
        const val MAX_TEXT = 1000
        const val MAX_APP_NAME = 100
        const val MAX_ACTION_TITLE = 40
        const val MAX_REPLY = 2000
        const val MAX_APP_ID = 255
        /** Remembered "last sent" entries (for skipping identical re-posts). */
        const val MAX_TRACKED = 500

        fun actionId(index: Int, action: PhoneAction): String {
            val digest = MessageDigest.getInstance("SHA-256")
                .digest("${if (action.isReply) "r" else "a"}|${action.title}".toByteArray(Charsets.UTF_8))
            return "$index-" + digest.take(4).joinToString("") { "%02x".format(it) }
        }

        /** Cuts to [max] UTF-16 units without splitting a surrogate pair. */
        fun clip(s: String, max: Int): String {
            if (s.length <= max) return s
            var end = max
            if (end > 0 && Character.isHighSurrogate(s[end - 1])) end--
            return s.substring(0, end).trimEnd() + "…"
        }
    }

    /** Last payload sent per key, to skip updates that change nothing visible. */
    private val lastSent = object : LinkedHashMap<String, NotificationPostPayload>(16, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, NotificationPostPayload>?) = size > MAX_TRACKED
    }

    /** True if this notification may be shown on (and acted on from) the PC. */
    fun shouldMirror(n: PhoneNotification): Boolean {
        if (n.appId == ownAppId) return false // never Bridge's own notifications
        if (n.ongoing) return false // media players, navigation, foreground services
        if (n.groupSummary) return false // the children carry the content
        if (n.secret) return false
        if (n.title.isNullOrBlank()) return false
        return filter.isAllowed(n.appId)
    }

    /** The buttons the PC may press: plain buttons and reply buttons, at most [MAX_ACTIONS], each with a title. */
    private fun offeredActions(n: PhoneNotification): List<Pair<String, PhoneAction>> =
        n.actions.withIndex()
            .filter { it.value.title.isNotBlank() }
            .take(MAX_ACTIONS)
            .map { actionId(it.index, it.value) to it.value }

    /**
     * Payload for a posted/updated notification, or null when it must not be
     * mirrored or nothing visible changed since the last one sent for this key.
     * [withActions]: the PC advertised notifications.actions.
     */
    fun onPosted(n: PhoneNotification, withActions: Boolean): NotificationPostPayload? {
        filter.noteSeen(n.appId, n.appName)
        if (!shouldMirror(n)) return null
        val actions = if (withActions) offeredActions(n).map { (id, a) ->
            NotificationActionInfo(id, clip(a.title, MAX_ACTION_TITLE), if (a.isReply) true else null)
        } else null
        val payload = NotificationPostPayload(
            appName = clip(n.appName, MAX_APP_NAME),
            title = clip(n.title!!, MAX_TITLE),
            text = clip(n.text, MAX_TEXT),
            key = n.key,
            appId = if (withActions) n.appId else null,
            actions = actions?.takeIf { it.isNotEmpty() },
            canDismiss = if (withActions) n.clearable else null,
        )
        synchronized(lastSent) {
            if (lastSent[n.key] == payload) return null
            lastSent[n.key] = payload
        }
        return payload
    }

    /**
     * What to send the PC for a posted/updated notification: the post, a
     * dismissal when a notification the PC is showing stopped qualifying (it
     * became ongoing or secret, or its app was turned off), or nothing.
     */
    fun update(n: PhoneNotification, withActions: Boolean): BridgeMessage? {
        onPosted(n, withActions)?.let { return BridgeMessage.create(MessageType.NOTIFICATION_POST, it) }
        if (shouldMirror(n)) return null // mirrored, just unchanged
        return synchronized(lastSent) { lastSent.remove(n.key) }
            ?.let { BridgeMessage.create(MessageType.NOTIFICATION_DISMISS, NotificationDismissPayload(n.key)) }
    }

    /**
     * Dismissal for a notification removed from the phone, or null if the PC
     * can't be showing it. Also covers toasts sent before Bridge restarted
     * (no longer tracked) as long as the notification qualified for mirroring.
     */
    fun onRemoved(n: PhoneNotification): NotificationDismissPayload? {
        val wasSent = synchronized(lastSent) { lastSent.remove(n.key) } != null
        return if (wasSent || shouldMirror(n)) NotificationDismissPayload(n.key) else null
    }

    /** The post for [key] didn't reach the PC (connection dropped): send it again next time. */
    fun forget(key: String) { synchronized(lastSent) { lastSent.remove(key) } }

    /**
     * Blocks or allows an app. Blocking returns dismissals for its notifications
     * the PC is showing, so they disappear there too.
     */
    fun setAppAllowed(appId: String, allowed: Boolean): List<NotificationDismissPayload> {
        filter.setAllowed(appId, allowed)
        if (allowed) return emptyList()
        val keys = source.active().filter { it.appId == appId }.map { it.key }.toSet()
        return synchronized(lastSent) { keys.filter { lastSent.remove(it) != null } }.map { NotificationDismissPayload(it) }
    }

    override suspend fun handleIncoming(message: BridgeMessage, reply: suspend (BridgeMessage) -> Unit) {
        when (message.type) {
            MessageType.NOTIFICATION_ACTION -> {
                val request = message.payloadAs<NotificationActionPayload>()
                val status = perform(request)
                reply(BridgeMessage.create(MessageType.NOTIFICATION_ACTION_RESULT, NotificationActionResultPayload(request.requestId, status)))
            }
            MessageType.NOTIFICATION_DISMISS -> {
                val key = message.payloadAs<NotificationDismissPayload>().key
                val n = source.find(key)
                if (n == null) {
                    // Already gone here: make sure the PC drops it as well.
                    forget(key)
                    reply(BridgeMessage.create(MessageType.NOTIFICATION_DISMISS, NotificationDismissPayload(key)))
                } else if (shouldMirror(n) && n.clearable) {
                    source.cancel(key) // onNotificationRemoved then tells the PC
                }
            }
            MessageType.NOTIFICATION_MUTE -> {
                val appId = message.payloadAs<NotificationMutePayload>().appId
                if (appId.isEmpty() || appId.length > MAX_APP_ID || appId == ownAppId) return
                for (d in setAppAllowed(appId, false)) reply(BridgeMessage.create(MessageType.NOTIFICATION_DISMISS, d))
            }
        }
    }

    private fun perform(request: NotificationActionPayload): String {
        val n = source.find(request.key) ?: return NotificationActionStatus.GONE
        if (!shouldMirror(n)) return NotificationActionStatus.REFUSED
        val action = offeredActions(n).firstOrNull { it.first == request.actionId }?.second
            ?: return NotificationActionStatus.GONE
        val text = request.replyText
        if (action.isReply) {
            if (text == null || text.isBlank() || text.length > MAX_REPLY) return NotificationActionStatus.INVALID
        } else if (text != null) {
            return NotificationActionStatus.INVALID
        }
        return try {
            if (action.perform(if (action.isReply) text else null)) NotificationActionStatus.OK else NotificationActionStatus.FAILED
        } catch (e: Exception) {
            NotificationActionStatus.FAILED // e.g. PendingIntent.CanceledException: the app withdrew it
        }
    }
}

/** Where per-app choices are kept (SharedPreferences on a device). */
interface NotificationFilterStore {
    fun load(): NotificationFilter.Saved
    fun save(saved: NotificationFilter.Saved)
}

/**
 * Per-app mirroring choices. Apps the user hasn't decided on follow
 * [Saved.allowNewApps] (on by default, matching earlier versions). [seen]
 * lists apps that posted notifications, for the settings screen.
 */
class NotificationFilter(private val store: NotificationFilterStore) {
    @Serializable data class Saved(
        val allowNewApps: Boolean = true,
        val decisions: Map<String, Boolean> = emptyMap(),
        val seen: Map<String, String> = emptyMap(), // appId → name
    )

    companion object { const val MAX_SEEN = 300 }

    private val lock = Any()
    private var saved: Saved = runCatching { store.load() }.getOrDefault(Saved())

    val snapshot: Saved get() = synchronized(lock) { saved }

    fun isAllowed(appId: String): Boolean = synchronized(lock) { saved.decisions[appId] ?: saved.allowNewApps }

    fun setAllowed(appId: String, allowed: Boolean) = update { it.copy(decisions = it.decisions + (appId to allowed)) }

    fun setAllowNewApps(allow: Boolean) = update { it.copy(allowNewApps = allow) }

    fun noteSeen(appId: String, appName: String) {
        synchronized(lock) { if (saved.seen[appId] == appName) return }
        update {
            val seen = LinkedHashMap(it.seen).apply { remove(appId); put(appId, appName) }
            while (seen.size > MAX_SEEN) seen.remove(seen.keys.first())
            it.copy(seen = seen)
        }
    }

    private inline fun update(change: (Saved) -> Saved) = synchronized(lock) {
        saved = change(saved)
        runCatching { store.save(saved) } // best effort (in order); the in-memory choice still applies
    }
}

class InMemoryNotificationFilterStore(private var saved: NotificationFilter.Saved = NotificationFilter.Saved()) : NotificationFilterStore {
    override fun load() = saved
    override fun save(saved: NotificationFilter.Saved) { this.saved = saved }
}
