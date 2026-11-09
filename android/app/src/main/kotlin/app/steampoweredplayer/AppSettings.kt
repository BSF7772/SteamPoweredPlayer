// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer

import android.content.Context
import app.steampoweredplayer.play.DecodeMode

/** The phone's own preferences. Nothing here reaches the share. */
class AppSettings(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences("settings", Context.MODE_PRIVATE)

    var decodeMode: DecodeMode
        get() = if (prefs.getString("decode", null) == DecodeMode.Cpu.name) DecodeMode.Cpu else DecodeMode.Gpu
        set(value) = prefs.edit().putString("decode", value.name).apply()

    var cacheGigabytes: Int
        get() = prefs.getInt("cache_gb", app.steampoweredplayer.data.CacheLimit.DEFAULT_GIGABYTES)
        set(value) = prefs.edit().putInt("cache_gb", value).apply()

    var exportChoice: String
        get() = prefs.getString("export_choice", null) ?: "medium"
        set(value) = prefs.edit().putString("export_choice", value).apply()

    var exportEngine: String
        get() = prefs.getString("export_engine", null) ?: "Auto"
        set(value) = prefs.edit().putString("export_engine", value).apply()

    /** The hidden speed-over-colour switch (plan §4.4): hardware decode for a re-encode, which can clip full-range input. */
    var fastReencode: Boolean
        get() = prefs.getBoolean("fast_reencode", false)
        set(value) = prefs.edit().putBoolean("fast_reencode", value).apply()

    var notificationsAsked: Boolean
        get() = prefs.getBoolean("notifications_asked", false)
        set(value) = prefs.edit().putBoolean("notifications_asked", value).apply()

    var activeProfileId: String?
        get() = prefs.getString("active_profile", null)
        set(value) = prefs.edit().putString("active_profile", value).apply()
}
