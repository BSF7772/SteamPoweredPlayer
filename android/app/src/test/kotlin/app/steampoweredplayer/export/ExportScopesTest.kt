// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import org.junit.Assert.assertEquals
import org.junit.Test

class ExportScopesTest {
    @Test
    fun nothingMarkedExportsTheWholeRecording() {
        assertEquals(ExportScope.Whole, ExportScopes.resolve(null, hasLoop = false, hasCuts = false))
    }

    @Test
    fun anABRangeIsExportedByDefault() {
        assertEquals(ExportScope.Loop, ExportScopes.resolve(null, hasLoop = true, hasCuts = false))
    }

    @Test
    fun cutsComeBeforeTheABRange() {
        assertEquals(ExportScope.Cuts, ExportScopes.resolve(null, hasLoop = true, hasCuts = true))
    }

    @Test
    fun theUsersChoiceWins() {
        assertEquals(ExportScope.Whole, ExportScopes.resolve(ExportScope.Whole, hasLoop = true, hasCuts = true))
        assertEquals(ExportScope.Loop, ExportScopes.resolve(ExportScope.Loop, hasLoop = true, hasCuts = true))
    }

    @Test
    fun aChoiceThatNoLongerExistsFallsBack() {
        assertEquals(ExportScope.Whole, ExportScopes.resolve(ExportScope.Loop, hasLoop = false, hasCuts = false))
        assertEquals(ExportScope.Loop, ExportScopes.resolve(ExportScope.Cuts, hasLoop = true, hasCuts = false))
    }

    @Test
    fun onlyWhatExistsIsOffered() {
        assertEquals(listOf(ExportScope.Whole), ExportScopes.available(hasLoop = false, hasCuts = false))
        assertEquals(listOf(ExportScope.Whole, ExportScope.Loop, ExportScope.Cuts), ExportScopes.available(hasLoop = true, hasCuts = true))
    }
}
