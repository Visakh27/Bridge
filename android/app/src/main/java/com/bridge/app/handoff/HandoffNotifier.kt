package com.bridge.app.handoff

import android.app.PendingIntent
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Handler
import android.os.Looper
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.bridge.app.R
import com.bridge.app.core.BridgeApplication

/**
 * Shows what the PC handed over. A link becomes a notification ("Link from
 * your PC" with the site's ASCII host) that opens it when tapped — Android
 * doesn't let background apps open pages, and the user should decide anyway.
 * Text goes on the clipboard, with a notification saying so.
 */
class HandoffNotifier(private val context: Context) {
    private val main = Handler(Looper.getMainLooper())
    private val counter = java.util.concurrent.atomic.AtomicInteger()

    /** Ids 2000–2049, reused in turn: the newest 50 handoffs stay, older ones are replaced. */
    private fun nextId() = 2000 + Math.floorMod(counter.getAndIncrement(), 50)

    fun showLink(link: HandoffLink) {
        val id = nextId()
        // BROWSABLE: only apps that handle web links from a browser can take it (no internal activities).
        val view = Intent(Intent.ACTION_VIEW, Uri.parse(link.url))
            .addCategory(Intent.CATEGORY_BROWSABLE)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        val open = PendingIntent.getActivity(context, id, view, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = NotificationCompat.Builder(context, BridgeApplication.HANDOFF_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_bridge)
            .setContentTitle("Link from your PC")
            .setContentText(link.title ?: link.host)
            .setSubText(link.host)
            .setContentIntent(open)
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_RECOMMENDATION)
            .build()
        notify(id, notification)
    }

    /** [write] puts it on the clipboard (through clipboard sync when connected, so it isn't echoed back). */
    fun copyText(text: String, write: ((String) -> Boolean)? = null) {
        if (write?.invoke(text) != true) main.post {
            runCatching {
                (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager)
                    .setPrimaryClip(ClipData.newPlainText("Bridge", text))
            }
        }
        notify(nextId(), NotificationCompat.Builder(context, BridgeApplication.HANDOFF_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_bridge)
            .setContentTitle("Copied from your PC")
            .setContentText(HandoffRules.preview(text))
            .setAutoCancel(true)
            .setTimeoutAfter(60_000)
            .build())
    }

    private fun notify(id: Int, n: android.app.Notification) {
        // Without the notification permission there's nowhere to show it; the PC was still answered.
        runCatching { NotificationManagerCompat.from(context).notify(id, n) }
    }
}
