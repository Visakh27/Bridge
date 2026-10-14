package com.bridge.app.notifications

import android.app.Notification
import android.app.RemoteInput
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import com.bridge.app.core.BridgeApplication
import com.bridge.app.service.BridgeConnectionService

/**
 * Section 11: captures notifications and forwards structured data (never a
 * screenshot) through BridgeConnectionService. The rules about what is
 * mirrored and what the PC may do live in [NotificationMirror]; this class
 * turns StatusBarNotifications into [PhoneNotification]s and carries out
 * button presses and replies. The user grants access in Settings ›
 * Notification access (the dashboard links there).
 */
class BridgeNotificationListener : NotificationListenerService() {

    companion object {
        /** The connected listener, while notification access is granted. */
        @Volatile var instance: BridgeNotificationListener? = null
            private set
    }

    private val mirror get() = (application as BridgeApplication).notificationMirror

    override fun onListenerConnected() { instance = this }

    override fun onListenerDisconnected() { if (instance === this) instance = null }

    override fun onDestroy() {
        if (instance === this) instance = null
        super.onDestroy()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        BridgeConnectionService.onPhoneNotificationPosted(mirror, toPhone(sbn))
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        BridgeConnectionService.onPhoneNotificationRemoved(mirror, toPhone(sbn))
    }

    /** Live lookup for [NotificationMirror]; null when gone or access was withdrawn. */
    fun find(key: String): PhoneNotification? =
        runCatching { getActiveNotifications(arrayOf(key))?.firstOrNull() }.getOrNull()?.let(::toPhone)

    fun all(): List<PhoneNotification> =
        runCatching { activeNotifications?.toList() }.getOrNull().orEmpty().map(::toPhone)

    fun cancel(key: String): Boolean = runCatching { cancelNotification(key) }.isSuccess

    private fun toPhone(sbn: StatusBarNotification): PhoneNotification {
        val n = sbn.notification
        val extras = n.extras
        val text = extras.getCharSequence(Notification.EXTRA_TEXT)?.toString()
            ?: extras.getCharSequence(Notification.EXTRA_BIG_TEXT)?.toString() ?: ""
        val appName = runCatching {
            packageManager.getApplicationLabel(packageManager.getApplicationInfo(sbn.packageName, 0)).toString()
        }.getOrDefault(sbn.packageName)
        return PhoneNotification(
            key = sbn.key,
            appId = sbn.packageName,
            appName = appName,
            title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString(),
            text = text,
            ongoing = sbn.isOngoing,
            groupSummary = n.flags and Notification.FLAG_GROUP_SUMMARY != 0,
            secret = n.visibility == Notification.VISIBILITY_SECRET,
            clearable = sbn.isClearable,
            actions = n.actions.orEmpty().mapNotNull(::toPhoneAction),
        )
    }

    private fun toPhoneAction(action: Notification.Action): PhoneAction? {
        val title = action.title?.toString()?.takeIf { it.isNotBlank() } ?: return null
        val intent = action.actionIntent ?: return null
        // Buttons that open an app would do so on the phone, out of the user's sight.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && intent.isActivity) return null
        // The app wants the phone unlocked for this one: don't let the PC bypass that.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && action.isAuthenticationRequired) return null
        // System-suggested smart replies/actions aren't the app's own buttons.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && action.isContextual) return null
        val inputs = action.remoteInputs.orEmpty()
        val freeForm = inputs.filter { it.allowFreeFormInput }
        if (inputs.isNotEmpty() && freeForm.isEmpty()) return null // choice-only inputs: not supported from the PC
        val isReply = freeForm.isNotEmpty()
        return PhoneAction(title, isReply) { replyText ->
            if (isReply) {
                val results = Bundle().apply { freeForm.forEach { putCharSequence(it.resultKey, replyText) } }
                val fill = Intent()
                RemoteInput.addResultsToIntent(inputs, fill, results)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) RemoteInput.setResultsSource(fill, RemoteInput.SOURCE_FREE_FORM_INPUT)
                intent.send(this, 0, fill)
            } else {
                intent.send()
            }
            true
        }
    }
}

/** [NotificationSource] backed by whichever listener instance is connected. */
object ListenerNotificationSource : NotificationSource {
    override fun find(key: String) = BridgeNotificationListener.instance?.find(key)
    override fun active() = BridgeNotificationListener.instance?.all().orEmpty()
    override fun cancel(key: String) = BridgeNotificationListener.instance?.cancel(key) ?: false
}
