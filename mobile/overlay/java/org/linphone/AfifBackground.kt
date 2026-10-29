/*
 * AFIF Phone — keep ringing with the screen off. GPLv3, part of AFIF Phone (based on Linphone).
 */
package org.linphone

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.PowerManager
import android.provider.Settings
import org.linphone.LinphoneApplication.Companion.corePreferences

object AfifBackground {
    private const val PREFS = "afif_phone"
    private const val ASKED = "battery_asked_v1"

    fun isUnrestricted(context: Context): Boolean =
        context.getSystemService(PowerManager::class.java)?.isIgnoringBatteryOptimizations(context.packageName) == true

    /**
     * With "Run in background" on, ask once to be left out of battery optimisation, so Android doesn't stop
     * AFIF Phone and calls and messages still arrive when the phone is locked.
     */
    fun check(activity: Activity) {
        if (!corePreferences.keepServiceAlive || isUnrestricted(activity)) return
        val prefs = activity.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        if (prefs.getBoolean(ASKED, false)) return
        activity.window.decorView.post {
            if (activity.isFinishing || activity.isDestroyed) return@post
            prefs.edit().putBoolean(ASKED, true).apply()
            AlertDialog.Builder(activity)
                .setTitle("Run in background")
                .setMessage(
                    "To receive calls and messages when the phone is locked and the screen is off, " +
                        "allow AFIF Phone to run without battery restrictions.\n\n" +
                        "You can switch this off any time in Settings → Advanced settings → Run in background."
                )
                .setPositiveButton("Allow") { _, _ -> request(activity) }
                .setNegativeButton("Not now", null)
                .show()
        }
    }

    fun request(activity: Activity) {
        try {
            activity.startActivity(
                Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:" + activity.packageName))
            )
        } catch (e: Exception) {
            try {
                activity.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
            } catch (_: Exception) { }
        }
    }
}
