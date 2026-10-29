/*
 * AFIF Phone — first-run choice for the built-in secure network (Tailscale engine).
 * GPLv3, part of AFIF Phone (based on Linphone).
 */
package org.linphone

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.Intent

object AfifSecureNetwork {
    private const val PREFS = "afif_phone"
    private const val ASKED = "secure_network_asked"
    private const val CHOSEN = "secure_network_enabled"

    fun open(activity: Activity) {
        try {
            activity.startActivity(Intent(activity, com.tailscale.ipn.MainActivity::class.java))
        } catch (e: Exception) {
            org.linphone.core.tools.Log.e("[AFIF] Can't open secure network: $e")
        }
    }

    fun isEnabled(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(CHOSEN, false)

    /**
     * Switch the secure network on or off for AFIF Phone (used by the SIP account's advanced settings).
     * On: connect, and show the secure network screen so the user can sign in the first time.
     */
    fun setEnabled(activity: Activity, on: Boolean) {
        activity.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putBoolean(CHOSEN, on).putBoolean(ASKED, true).apply()
        try {
            activity.sendBroadcast(
                Intent(if (on) "com.tailscale.ipn.CONNECT_VPN" else "com.tailscale.ipn.DISCONNECT_VPN")
                    .setPackage(activity.packageName)
            )
        } catch (e: Exception) {
            org.linphone.core.tools.Log.e("[AFIF] Secure network switch failed: $e")
        }
        if (on) open(activity)
    }

    /** A "Connect through the secure network (Tailscale)" check box wired to the setting. */
    fun bind(activity: Activity, box: android.widget.CompoundButton?) {
        if (box == null) return // tablet layouts without the option
        box.text = "Connect through the secure network (Tailscale)"
        box.isChecked = isEnabled(activity)
        box.setOnCheckedChangeListener { _, on -> setEnabled(activity, on) }
    }

    /** Ask once, on first setup, whether to connect the secure network. */
    fun askOnce(activity: Activity) {
        val prefs = activity.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        if (prefs.getBoolean(ASKED, false)) {
            AfifBackground.check(activity)
            return
        }
        activity.window.decorView.post {
            if (activity.isFinishing || activity.isDestroyed || prefs.getBoolean(ASKED, false)) return@post
            prefs.edit().putBoolean(ASKED, true).apply()
            AlertDialog.Builder(activity)
                .setTitle("Secure network")
                .setMessage(
                    "Do you want to connect AFIF Phone to your secure network (Tailscale)?\n\n" +
                        "Choose this to reach your AFIF-PBX extension when you are away from home. " +
                        "At home on the same Wi-Fi you don't need it.\n\n" +
                        "You can change this any time from the menu → Secure network."
                )
                .setPositiveButton("Connect") { _, _ -> setEnabled(activity, true) }
                .setNegativeButton("Not now") { _, _ ->
                    prefs.edit().putBoolean(CHOSEN, false).apply()
                    AfifBackground.check(activity)
                }
                .setCancelable(true)
                .show()
        }
    }
}
