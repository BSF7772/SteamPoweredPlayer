// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.content.Context
import androidx.annotation.StringRes
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import app.steampoweredplayer.MainActivity
import app.steampoweredplayer.R
import app.steampoweredplayer.net.Downloads
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class MeteredNoticeTest {
    @get:Rule val compose = createEmptyComposeRule()

    private val context: Context get() = InstrumentationRegistry.getInstrumentation().targetContext

    private fun text(@StringRes id: Int) = context.getString(id)

    @Before
    fun reset() {
        context.getSharedPreferences("downloads", Context.MODE_PRIVATE).edit().clear().commit()
        Downloads.closeMeteredNotice(context, forever = false)
    }

    private fun assertNoticeGone() {
        compose.waitForIdle()
        assertTrue(compose.onAllNodes(hasText(text(R.string.metered_notice))).fetchSemanticsNodes().isEmpty())
    }

    @Test
    fun okClosesItForNow() {
        ActivityScenario.launch(MainActivity::class.java).use {
            assertNoticeGone()
            Downloads.showMeteredNotice()
            compose.onNodeWithText(text(R.string.metered_notice)).assertExists()
            compose.onNodeWithText(text(R.string.ok)).performClick()
            assertNoticeGone()
        }
        assertFalse(Downloads.noticeDismissed(context))
    }

    @Test
    fun dontShowAgainIsRemembered() {
        ActivityScenario.launch(MainActivity::class.java).use {
            Downloads.showMeteredNotice()
            compose.onNodeWithText(text(R.string.dont_show_again)).performClick()
            assertNoticeGone()
        }
        assertTrue(Downloads.noticeDismissed(context))
    }
}
