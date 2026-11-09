// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import androidx.annotation.StringRes
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import app.steampoweredplayer.MainActivity
import app.steampoweredplayer.R
import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/** The add-server screen on a device, with the real profile file and vault. TEST-NET-1 (192.0.2.0/24) never answers, so nothing real is contacted. */
@RunWith(AndroidJUnit4::class)
class AddServerTest {
    @get:Rule val compose = createEmptyComposeRule()

    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val servers get() = File(context.filesDir, "servers.json")

    private fun text(@StringRes id: Int) = context.getString(id)

    @Before
    fun clearSavedServers() {
        servers.delete()
        File(context.noBackupFilesDir, "vault").deleteRecursively()
    }

    private fun openEditor() {
        compose.onNodeWithText(text(R.string.add_server)).performClick()
    }

    private fun fillIn(name: String, host: String) {
        compose.onNodeWithText(text(R.string.field_name)).performTextInput(name)
        compose.onNodeWithText(text(R.string.field_host)).performTextInput(host)
    }

    @Test
    fun anEmptyLibraryAsksForAServer() {
        ActivityScenario.launch(MainActivity::class.java).use {
            compose.onNodeWithText(text(R.string.home_empty_title)).assertExists()
            compose.onNodeWithText(text(R.string.add_server)).assertExists()
        }
    }

    @Test
    fun theFormAsksOnlyForWhatAPersonKnows() {
        ActivityScenario.launch(MainActivity::class.java).use {
            openEditor()
            for (id in listOf(R.string.field_name, R.string.field_host, R.string.field_user, R.string.field_password)) compose.onNodeWithText(text(id)).assertExists()
            compose.onNodeWithText(text(R.string.field_share)).assertDoesNotExist()
        }
    }

    @Test
    fun connectStaysOffUntilTheServerIsValid() {
        ActivityScenario.launch(MainActivity::class.java).use {
            openEditor()
            val connect = compose.onNodeWithText(text(R.string.connect))
            connect.assertIsNotEnabled()
            fillIn("Test", "bad host")
            connect.assertIsNotEnabled()
        }
    }

    @Test
    fun backLeavesNothingBehind() {
        ActivityScenario.launch(MainActivity::class.java).use {
            openEditor()
            fillIn("Test", "192.0.2.1")
            compose.onNodeWithContentDescription("Back").performClick()
            compose.onNodeWithText(text(R.string.home_empty_title)).assertExists()
        }
        assertFalse(servers.exists())
    }

    @Test
    fun aServerThatDoesntAnswerIsReportedAndNotSaved() {
        ActivityScenario.launch(MainActivity::class.java).use {
            openEditor()
            fillIn("Test", "192.0.2.1")
            compose.onNodeWithText(text(R.string.connect)).assertIsEnabled().performClick()
            compose.waitUntil(WAIT_MS) { compose.onAllNodes(hasText(text(R.string.unreachable))).fetchSemanticsNodes().isNotEmpty() }
        }
        assertFalse(servers.exists())
    }

    @Test
    fun aTypedShareIsSavedAndIsThereAfterARestart() {
        ActivityScenario.launch(MainActivity::class.java).use {
            openEditor()
            fillIn("Test server", "192.0.2.1")
            compose.onNodeWithText(text(R.string.field_password)).performTextInput("not-a-real-password")
            compose.onNodeWithText(text(R.string.advanced)).performScrollTo().performClick()
            compose.onNodeWithText(text(R.string.field_share)).performScrollTo().performTextInput("share")
            compose.onNodeWithText(text(R.string.connect)).assertIsEnabled().performClick()
            compose.waitUntil(WAIT_MS) { compose.onAllNodes(hasText("Test server", substring = true)).fetchSemanticsNodes().isNotEmpty() }
            compose.waitUntil(WAIT_MS) { servers.exists() }
        }
        assertFalse("the password must not reach the profile file", servers.readText().contains("not-a-real-password"))
        ActivityScenario.launch(MainActivity::class.java).use {
            // The app opens on the library, whose header names the server it's using; the server's own row is in the settings.
            compose.waitUntil(WAIT_MS) { compose.onAllNodes(hasText("Test server", substring = true)).fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("Settings").performClick()
            compose.waitUntil(WAIT_MS) { compose.onAllNodes(hasText("Test server")).fetchSemanticsNodes().isNotEmpty() }
        }
    }

    private companion object {
        const val WAIT_MS = 15_000L
    }
}
