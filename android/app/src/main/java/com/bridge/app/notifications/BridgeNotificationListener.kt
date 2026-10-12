package com.bridge.app.notifications

import android.app.Notification
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import com.bridge.app.protocol.BridgeMessage
import com.bridge.app.protocol.MessageType
import com.bridge.app.protocol.NotificationDismissPayload
import com.bridge.app.protocol.NotificationPostPayload
import com.bridge.app.service.BridgeConnectionService

/**
 * Section 11: captures eligible notifications and forwards structured data
 * (never a screenshot) to the active session via BridgeConnectionService.
 * The user grants access in Settings › Notification access (the dashboard
 * links there).
 */
class BridgeNotificationListener : NotificationListenerService() {

    private fun shouldMirror(sbn: StatusBarNotification): Boolean {
        if (sbn.packageName == packageName) return false // don't mirror Bridge's own notifications
        if (sbn.isOngoing) return false // media players, navigation, other foreground services
        val n = sbn.notification
        if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return false // the children carry the content
        if (n.visibility == Notification.VISIBILITY_SECRET) return false
        return true
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (!shouldMirror(sbn)) return
        val extras = sbn.notification.extras
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString() ?: return
        val text = extras.getCharSequence(Notification.EXTRA_TEXT)?.toString() ?: ""
        val appName = runCatching {
            packageManager.getApplicationLabel(packageManager.getApplicationInfo(sbn.packageName, 0)).toString()
        }.getOrDefault(sbn.packageName)

        BridgeConnectionService.instance?.sendMessage(
            BridgeMessage.create(MessageType.NOTIFICATION_POST, NotificationPostPayload(appName, title, text, sbn.key))
        )
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        if (!shouldMirror(sbn)) return
        BridgeConnectionService.instance?.sendMessage(
            BridgeMessage.create(MessageType.NOTIFICATION_DISMISS, NotificationDismissPayload(sbn.key))
        )
    }
}
