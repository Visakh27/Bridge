package com.bridge.app.service

import android.content.Context
import android.net.Uri
import android.os.StatFs
import androidx.documentfile.provider.DocumentFile
import com.bridge.app.network.FileTransferClient
import com.bridge.app.network.FolderItem
import com.bridge.app.network.SafeFileName
import com.bridge.app.network.SafeRelativePath
import java.io.File
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Turns a folder picked with ACTION_OPEN_DOCUMENT_TREE into [FolderItem]s for
 * FileTransferClient.sendFolder: walks it with DocumentFile (depth-capped),
 * copies each file to private cache, and records its folder path.
 */
class FolderCopier(private val context: Context) {
    private val cacheDir = File(context.cacheDir, "share").apply { mkdirs() }

    suspend fun copy(treeUri: Uri): List<FolderItem> = withContext(Dispatchers.IO) {
        val root = DocumentFile.fromTreeUri(context, treeUri) ?: throw IOException("Couldn't open that folder.")
        val rootName = SafeFileName.sanitize(root.name ?: "Folder")
        val found = mutableListOf<Pair<DocumentFile, String>>()
        fun walk(dir: DocumentFile, path: String, depth: Int) {
            if (depth >= SafeRelativePath.MAX_DEPTH) return
            for (child in dir.listFiles().sortedBy { it.name }) {
                when {
                    child.isDirectory -> walk(child, "$path/${SafeFileName.sanitize(child.name)}", depth + 1)
                    child.isFile -> found += child to path
                }
                if (found.size > FileTransferClient.MAX_FOLDER_FILES) throw IOException("That folder has more than ${FileTransferClient.MAX_FOLDER_FILES} files.")
            }
        }
        walk(root, rootName, 0)

        val needed = found.sumOf { it.first.length() }
        val free = StatFs(cacheDir.path).availableBytes
        if (needed > free * 9 / 10) throw IOException("Not enough free space on the phone to send that folder.")

        val items = mutableListOf<FolderItem>()
        try {
            for ((doc, path) in found) {
                val copy = File(cacheDir, UUID.randomUUID().toString())
                context.contentResolver.openInputStream(doc.uri)?.use { input -> copy.outputStream().use { input.copyTo(it) } }
                    ?: continue // unreadable entry: skip it
                // The copy's name is random; the name the PC sees comes from the original.
                items += FolderItem(copy, path, deleteWhenDone = true, name = SafeFileName.sanitize(doc.name))
            }
        } catch (e: Exception) {
            items.forEach { it.file.delete() }
            throw e
        }
        items
    }
}
