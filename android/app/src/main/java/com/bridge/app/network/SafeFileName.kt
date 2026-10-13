package com.bridge.app.network

import java.io.File

/**
 * Turns a peer-supplied file name into a safe leaf name inside the download
 * folder. The name in file.start is attacker-controlled: without this,
 * "../../shared_prefs/x.xml" would be written outside the download folder.
 * Mirrors Bridge.Network.SafeFileName on Windows.
 */
object SafeFileName {
    /** Android filesystems (ext4, f2fs) cap a name at 255 *bytes*; leave room for " (n)". */
    private const val MAX_BYTES = 200
    private val INVALID = setOf('<', '>', ':', '"', '|', '?', '*', '/', '\\')

    fun sanitize(name: String?): String {
        var leaf = (name ?: "").replace('\\', '/').substringAfterLast('/')
        leaf = buildString {
            for (c in leaf) {
                // Invisible format characters (RTL override U+202E, zero-width
                // spaces, BOM…) are dropped: "photo\u202Egnp.apk" displays as "photokpa.png".
                if (Character.getType(c) == Character.FORMAT.toInt()) continue
                append(if (c.isISOControl() || c in INVALID) '_' else c)
            }
        }
        var cleaned = leaf.trim().trimEnd('.', ' ')
        if (cleaned.isEmpty() || cleaned.all { it == '.' }) cleaned = "received_file"
        if (cleaned.utf8Size() > MAX_BYTES) {
            val dot = cleaned.lastIndexOf('.')
            val ext = if (dot > 0 && cleaned.length - dot <= 20) cleaned.substring(dot) else ""
            cleaned = truncateUtf8(cleaned.substring(0, cleaned.length - ext.length), MAX_BYTES - ext.utf8Size()) + ext
        }
        return cleaned
    }

    private fun String.utf8Size() = toByteArray(Charsets.UTF_8).size

    /** Longest prefix of [s] that fits in [maxBytes] of UTF-8, never splitting a code point. */
    private fun truncateUtf8(s: String, maxBytes: Int): String {
        var bytes = 0
        var i = 0
        while (i < s.length) {
            val cp = s.codePointAt(i)
            val n = String(Character.toChars(cp)).utf8Size()
            if (bytes + n > maxBytes) break
            bytes += n
            i += Character.charCount(cp)
        }
        return s.substring(0, i)
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
