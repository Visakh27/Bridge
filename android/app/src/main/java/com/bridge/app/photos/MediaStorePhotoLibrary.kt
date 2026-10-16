package com.bridge.app.photos

import android.Manifest
import android.content.ContentResolver
import android.content.ContentUris
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Build
import android.os.Bundle
import android.provider.MediaStore
import android.util.Size
import androidx.core.content.ContextCompat
import com.bridge.app.network.SafeFileName
import com.bridge.app.protocol.PhotoInfo
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileNotFoundException
import java.util.UUID

/**
 * [PhotoLibrary] over Android's MediaStore (images only). Everything is
 * looked up by MediaStore id through the content resolver, so the PC can only
 * reach photos Android lets Bridge see — on Android 14 with "selected photos"
 * access, just those. Ordered by date added (always set; date taken can be
 * missing), newest first.
 */
class MediaStorePhotoLibrary(private val context: Context, private val exportDir: File) : PhotoLibrary {
    private val resolver: ContentResolver get() = context.contentResolver
    private val collection = MediaStore.Images.Media.EXTERNAL_CONTENT_URI

    companion object {
        /** The permissions to ask for, by Android version. */
        fun permissions(): Array<String> = when {
            Build.VERSION.SDK_INT >= 34 -> arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED)
            Build.VERSION.SDK_INT >= 33 -> arrayOf(Manifest.permission.READ_MEDIA_IMAGES)
            else -> arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
        }

        fun hasPermission(context: Context): Boolean = permissions().any {
            ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED
        }
    }

    override fun hasPermission() = hasPermission(context)

    override fun page(before: PhotoCursor?, limit: Int): List<LibraryPhoto> {
        val projection = arrayOf(
            MediaStore.Images.Media._ID, MediaStore.Images.Media.DISPLAY_NAME, MediaStore.Images.Media.DATE_TAKEN,
            MediaStore.Images.Media.DATE_ADDED, MediaStore.Images.Media.WIDTH, MediaStore.Images.Media.HEIGHT,
            MediaStore.Images.Media.SIZE, MediaStore.Images.Media.MIME_TYPE,
        )
        val added = MediaStore.Images.Media.DATE_ADDED
        val id = MediaStore.Images.Media._ID
        val selection = before?.let { "($added < ?) OR ($added = ? AND $id < ?)" }
        val args = before?.let { arrayOf((it.sortKey / 1000).toString(), (it.sortKey / 1000).toString(), it.id.toString()) }
        val order = "$added DESC, $id DESC"
        val cursor = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            resolver.query(collection, projection, Bundle().apply {
                putString(ContentResolver.QUERY_ARG_SQL_SELECTION, selection)
                putStringArray(ContentResolver.QUERY_ARG_SQL_SELECTION_ARGS, args)
                putString(ContentResolver.QUERY_ARG_SQL_SORT_ORDER, order)
                putInt(ContentResolver.QUERY_ARG_LIMIT, limit)
            }, null)
        } else {
            resolver.query(collection, projection, selection, args, "$order LIMIT $limit")
        }
        val out = ArrayList<LibraryPhoto>(limit)
        cursor?.use { c ->
            while (c.moveToNext() && out.size < limit) {
                val photoId = c.getLong(0)
                val addedMs = c.getLong(3) * 1000
                val taken = if (c.isNull(2)) addedMs else c.getLong(2)
                out += LibraryPhoto(PhotoInfo(
                    id = photoId.toString(),
                    name = SafeFileName.sanitize(c.getString(1) ?: "photo_$photoId.jpg"),
                    takenAt = taken,
                    width = c.getInt(4), height = c.getInt(5),
                    size = c.getLong(6),
                    mime = c.getString(7) ?: "image/*",
                ), sortKey = addedMs)
            }
        }
        return out
    }

    private fun exists(id: Long): Boolean =
        resolver.query(collection, arrayOf(MediaStore.Images.Media._ID), "${MediaStore.Images.Media._ID} = ?", arrayOf(id.toString()), null)
            ?.use { it.moveToFirst() } ?: false

    override fun thumbnail(id: Long, maxSide: Int): ByteArray? {
        if (!exists(id)) return null
        val uri = ContentUris.withAppendedId(collection, id)
        val bitmap = try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) resolver.loadThumbnail(uri, Size(maxSide, maxSide), null)
            else resolver.openInputStream(uri)?.use { input ->
                // Decode at a reduced size: a 50 MP photo must not be decoded in full for a preview.
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
                var sample = 1
                while (maxOf(bounds.outWidth, bounds.outHeight) / (sample * 2) >= maxSide) sample *= 2
                BitmapFactory.decodeStream(input, null, BitmapFactory.Options().apply { inSampleSize = sample })
            }
        } catch (e: FileNotFoundException) {
            null
        } ?: return null
        val scale = maxSide.toFloat() / maxOf(bitmap.width, bitmap.height)
        val fitted = if (scale < 1f) Bitmap.createScaledBitmap(bitmap, (bitmap.width * scale).toInt().coerceAtLeast(1),
            (bitmap.height * scale).toInt().coerceAtLeast(1), true) else bitmap
        return ByteArrayOutputStream().use { out ->
            fitted.compress(Bitmap.CompressFormat.JPEG, 75, out)
            if (fitted !== bitmap) fitted.recycle()
            bitmap.recycle()
            out.toByteArray()
        }
    }

    override fun export(id: Long): ExportedPhoto? {
        val name = resolver.query(collection, arrayOf(MediaStore.Images.Media.DISPLAY_NAME), "${MediaStore.Images.Media._ID} = ?",
            arrayOf(id.toString()), null)?.use { if (it.moveToFirst()) it.getString(0) ?: "photo_$id.jpg" else null } ?: return null
        exportDir.mkdirs()
        val copy = File(exportDir, UUID.randomUUID().toString())
        return try {
            resolver.openInputStream(ContentUris.withAppendedId(collection, id))?.use { input ->
                copy.outputStream().use { input.copyTo(it) }
            } ?: return null
            ExportedPhoto(copy, SafeFileName.sanitize(name))
        } catch (e: FileNotFoundException) {
            copy.delete(); null
        } catch (e: Exception) {
            copy.delete(); throw e
        }
    }
}
