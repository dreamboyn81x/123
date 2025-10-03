package org.kenjinx.android

import android.content.Intent
import kotlin.math.abs

class PerformanceManager(private val activity: MainActivity) {
    companion object {
        fun force60HzRefreshRate(enable: Boolean, activity: MainActivity) {
            // Hack for MIUI devices since they don't support the standard Android APIs
            try {
                val setFpsIntent = Intent("com.miui.powerkeeper.SET_ACTIVITY_FPS").apply {
                    putExtra("package_name", "org.kenjinx.android")
                    putExtra("isEnter", enable)
                }
                activity.sendBroadcast(setFpsIntent)
            } catch (_: Exception) {
            }

            if (enable)
                activity.display?.supportedModes?.minByOrNull { abs(it.refreshRate - 60f) }
                    ?.let { activity.window.attributes.preferredDisplayModeId = it.modeId }
            else
                activity.display?.supportedModes?.maxByOrNull { it.refreshRate }
                    ?.let { activity.window.attributes.preferredDisplayModeId = it.modeId }
        }
    }

    fun setTurboMode(enable: Boolean) {
        NativeHelpers.instance.setTurboMode(enable)
        force60HzRefreshRate(enable, activity)
    }
}
