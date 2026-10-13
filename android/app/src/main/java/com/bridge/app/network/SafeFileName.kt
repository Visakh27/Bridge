package com.bridge.app.network

import java.io.File

/**
 * Turns a peer-supplied file name into a safe leaf name inside the download
 * folder. The name in file.start is attacker-controlled: without this,
 * "../../shared_prefs/x.xml" would be written outside the download folder.
 * Mirrors Bridge.Network.SafeFileName on Windows.
 */
object SafeFileName {
    private const val MAX_LENGTH = 200
    private val INVALID = setOf('<', '>', ':', '"', '|', '?', '*', '/', '\\')

    fun sanitize(name: String?): String {
        var leaf = (name ?: "").replace('\\', '/').substringAfterLast('/')
        leaf = buildString { leaf.forEach { append(if (it.isISOControl() || it in INVALID) '_' else it) } }
        var cleaned = leaf.trim().trimEnd('.', ' ')
        if (cleaned.isEmpty() || cleaned.all { it == '.' }) cleaned = "received_file"
        if (cleaned.length > MAX_LENGTH) {
            val ext = cleaned.substringAfterLast('.', "").let { if (it.isEmpty() || it.length > 20) "" else ".$it" }
            cleaned = cleaned.take(MAX_LENGTH - ext.length) + ext
        }
        return cleaned
    }

    /** A file in [directory] that doesn't exist yet: "name.ext", then "name (1).ext", … */
    fun uniqueFile(directory: File, safeName: String): File {
        var candidate = File(directory, safeName)
        if (!candidate.exists()) return candidate
        val dot = safeName.lastIndexOf('.')
        val stem = if (dot > 0) safeName.substring(0, dot) else safeName
        val ext = if (dot > 0) safeName.substring(dot) else ""
        var i = 1
        while (true) {
            candidate = File(directory, "$stem ($i)$ext")
            if (!candidate.exists()) return candidate
            i++
        }
    }
}
