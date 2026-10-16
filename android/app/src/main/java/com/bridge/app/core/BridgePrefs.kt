package com.bridge.app.core

import android.content.Context

/** Small app settings kept in SharedPreferences ("bridge_settings", shared with clipboard sync). */
object BridgePrefs {
    private const val FILE = "bridge_settings"
    private const val ONBOARDING_DONE = "onboarding_done"
    private const val DIAGNOSTICS = "diagnostics"
    private const val PHOTOS = "photos_enabled"

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    fun onboardingDone(context: Context) = prefs(context).getBoolean(ONBOARDING_DONE, false)
    fun setOnboardingDone(context: Context) = prefs(context).edit().putBoolean(ONBOARDING_DONE, true).apply()

    /** Diagnostics log is on by default (metadata only; it stays on the phone unless shared). */
    fun diagnosticsEnabled(context: Context) = prefs(context).getBoolean(DIAGNOSTICS, true)
    fun setDiagnosticsEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(DIAGNOSTICS, enabled).apply()
        val log = (context.applicationContext as BridgeApplication).diagnostics
        if (!enabled) { log.enabled = false; log.clear() } // off means nothing kept
        else { log.enabled = true; log.info("diagnostics.enabled") }
    }

    /** "Let your PC browse photos": off until the user turns it on. */
    fun photosEnabled(context: Context) = prefs(context).getBoolean(PHOTOS, false)
    fun setPhotosEnabled(context: Context, enabled: Boolean) = prefs(context).edit().putBoolean(PHOTOS, enabled).apply()

    fun appVersion(context: Context): String =
        runCatching { context.packageManager.getPackageInfo(context.packageName, 0).versionName }.getOrNull() ?: "?"
}
