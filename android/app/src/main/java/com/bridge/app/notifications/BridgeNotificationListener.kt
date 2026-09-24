package com.bridge.app.notifications

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
 */
class BridgeNotificationListener : NotificationListenerService() {

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.packageName == packageName) return // don't mirror Bridge's own notifications
        val extras = sbn.notification.extras
        val title = extras.getCharSequence(android.app.Notification.EXTRA_TITLE)?.toString() ?: return
        val text = extras.getCharSequence(android.app.Notification.EXTRA_TEXT)?.toString() ?: ""
        val key = sbn.key

        val message = BridgeMessage.create(
            MessageType.NOTIFICATION_POST,
            NotificationPostPayload(appName = sbn.packageName, title = title, text = text, key = key)
        )
        BridgeConnectionService.instance?.sendMessage(message)
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        val message = BridgeMessage.create(MessageType.NOTIFICATION_DISMISS, NotificationDismissPayload(sbn.key))
        BridgeConnectionService.instance?.sendMessage(message)
    }
}
