package com.bridge.app.core

import java.io.File
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Instant
import java.time.format.DateTimeFormatter

/**
 * One field of a diagnostics event. Only these shapes exist, so nothing the
 * user typed, copied or received (file names, clipboard text, notification
 * text, device names, keys, tokens) can be written by accident: numbers,
 * flags, short code words (statuses, exception type names) and identifiers,
 * which are logged as a short salted hash. Mirrors Bridge.Core.LogField.
 */
class LogField private constructor(val key: String, val value: String) {
    companion object {
        private val CODE = Regex("^[A-Za-z_][A-Za-z0-9_-]{0,31}$")
        private val VERSION = Regex("^[0-9]{1,6}(\\.[0-9]{1,6}){0,3}$")

        fun num(key: String, value: Long) = LogField(key, value.toString())
        fun num(key: String, value: Int) = LogField(key, value.toString())
        fun flag(key: String, value: Boolean) = LogField(key, value.toString())
        fun ms(key: String, millis: Long) = LogField(key, millis.toString())

        /** A code word such as a status or an exception type name; anything else becomes "?". */
        fun code(key: String, value: String?) = LogField(key, if (value != null && CODE.matches(value)) value else "?")

        /** A version like 0.2.0 or 34. */
        fun version(key: String, value: String?) = LogField(key, if (value != null && VERSION.matches(value)) value else "?")

        /** A device/transfer id as 8 hex chars of a salted hash (correlates within one run, reveals nothing). */
        fun id(key: String, value: String?, log: DiagnosticsLog) = LogField(key, value?.let(log::hashId) ?: "-")

        /** An exception: its class name only (messages can contain paths and names). */
        fun error(e: Throwable) = code("error", e.javaClass.simpleName)

        internal fun validKey(key: String) = CODE.matches(key)
    }
}

/**
 * Diagnostics log (docs/security.md, Logging): metadata only — never
 * contents. `bridge.log` in [directory], rotated at [MAX_FILE_BYTES] keeping
 * [FILES] files; files older than [RETENTION_MS] are deleted. Thread-safe;
 * never throws. [directory] null keeps lines in memory (tests).
 */
class DiagnosticsLog(val directory: File?, private val now: () -> Long = System::currentTimeMillis) {
    companion object {
        const val MAX_FILE_BYTES = 256L * 1024
        const val FILES = 3
        const val RETENTION_MS = 14L * 24 * 60 * 60 * 1000
        const val FILE_NAME = "bridge.log"
        private val EVENT = Regex("^[a-z][a-z0-9_.]{0,47}$")
    }

    private val lock = Any()
    private val salt = ByteArray(16).also { SecureRandom().nextBytes(it) }
    private val memory: MutableList<String>? = if (directory == null) mutableListOf() else null

    @Volatile var enabled: Boolean = true

    init {
        val dir = directory
        if (dir != null) runCatching {
            dir.mkdirs()
            logFiles().filter { now() - it.lastModified() > RETENTION_MS }.forEach { it.delete() }
        }
    }

    fun hashId(value: String): String {
        val d = MessageDigest.getInstance("SHA-256").apply { update(salt); update(value.toByteArray(Charsets.UTF_8)) }.digest()
        return d.take(4).joinToString("") { "%02x".format(it) }
    }

    fun info(event: String, vararg fields: LogField) = write("INFO", event, fields)
    fun warn(event: String, vararg fields: LogField) = write("WARN", event, fields)

    private fun write(level: String, event: String, fields: Array<out LogField>) {
        if (!enabled) return
        val sb = StringBuilder(96)
            .append(DateTimeFormatter.ISO_INSTANT.format(Instant.ofEpochMilli(now())))
            .append(' ').append(level).append(' ').append(if (EVENT.matches(event)) event else "invalid_event")
        for (f in fields) if (LogField.validKey(f.key)) sb.append(' ').append(f.key).append('=').append(f.value)
        val line = sb.toString()
        synchronized(lock) {
            memory?.let { it += line; if (it.size > 5000) it.removeAt(0); return }
            try {
                val file = File(directory, FILE_NAME)
                if (file.exists() && file.length() + line.length + 1 > MAX_FILE_BYTES) rotate()
                file.appendText(line + "\n")
            } catch (e: Exception) { /* diagnostics must never break the app */ }
        }
    }

    private fun rotate() {
        File(directory, "bridge.${FILES - 1}.log").delete()
        for (i in FILES - 2 downTo 1) File(directory, "bridge.$i.log").let { if (it.exists()) it.renameTo(File(directory, "bridge.${i + 1}.log")) }
        File(directory, FILE_NAME).renameTo(File(directory, "bridge.1.log"))
    }

    /** Log files, oldest first. */
    fun logFiles(): List<File> {
        val dir = directory ?: return emptyList()
        return ((FILES - 1 downTo 1).map { File(dir, "bridge.$it.log") } + File(dir, FILE_NAME)).filter { it.exists() }
    }

    /** All logged lines, oldest first. */
    fun lines(): List<String> = synchronized(lock) {
        memory?.toList() ?: logFiles().flatMap { runCatching { it.readLines() }.getOrDefault(emptyList()) }
    }

    /** Deletes all logs (when the user turns diagnostics off). */
    fun clear() = synchronized(lock) { memory?.clear(); logFiles().forEach { it.delete() } }
}

/** A plain-text report to attach to a bug report: a few safe facts plus the log. */
object DiagnosticsReport {
    fun build(log: DiagnosticsLog, nowMillis: Long, vararg facts: LogField): String = buildString {
        appendLine("Bridge diagnostics report")
        appendLine("generated=${DateTimeFormatter.ISO_INSTANT.format(Instant.ofEpochMilli(nowMillis))}")
        for (f in facts) appendLine("${f.key}=${f.value}")
        appendLine("This report contains connection and transfer events only: no file names, messages, clipboard text or keys.")
        appendLine("--- log ---")
        for (line in log.lines()) appendLine(line)
    }
}
