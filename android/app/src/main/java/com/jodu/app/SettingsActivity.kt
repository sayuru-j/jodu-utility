package com.jodu.app

import android.content.Intent
import android.os.Bundle
import android.provider.Settings
import android.util.TypedValue
import android.view.View
import android.widget.Button
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding

class SettingsActivity : AppCompatActivity() {
    private lateinit var backgroundRunHint: TextView
    private lateinit var btnBackgroundRun: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        setContentView(R.layout.activity_settings)

        val root = findViewById<View>(R.id.settingsRoot)
        val sidePad = dp(20)
        val basePad = dp(16)
        ViewCompat.setOnApplyWindowInsetsListener(root) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.updatePadding(
                left = sidePad + bars.left,
                top = bars.top,
                right = sidePad + bars.right,
                bottom = basePad + bars.bottom,
            )
            insets
        }

        backgroundRunHint = findViewById(R.id.backgroundRunHint)
        btnBackgroundRun = findViewById(R.id.btnBackgroundRun)

        findViewById<Button>(R.id.btnCloseSettings).setOnClickListener { finish() }
        findViewById<Button>(R.id.btnSettingsNotifications).setOnClickListener {
            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
        btnBackgroundRun.setOnClickListener {
            BatteryOptimizationHelper.requestUnrestrictedBackground(this)
        }
    }

    override fun onResume() {
        super.onResume()
        refreshBackgroundState()
    }

    private fun refreshBackgroundState() {
        val granted = BatteryOptimizationHelper.isIgnoringBatteryOptimizations(this)
        btnBackgroundRun.text = getString(
            if (granted) R.string.background_run_granted else R.string.action_background_run,
        )
        backgroundRunHint.text = getString(
            if (granted) R.string.background_run_granted else R.string.background_run_hint,
        )
    }

    private fun dp(value: Int): Int =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value.toFloat(), resources.displayMetrics).toInt()
}
