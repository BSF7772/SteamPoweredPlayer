// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

/** Which part of a recording an export takes. */
enum class ExportScope { Whole, Loop, Cuts }

/** Pure. */
object ExportScopes {
    /** What's on offer: the whole recording always, the A-B range and the cuts when they exist. */
    fun available(hasLoop: Boolean, hasCuts: Boolean): List<ExportScope> = buildList {
        add(ExportScope.Whole)
        if (hasLoop) add(ExportScope.Loop)
        if (hasCuts) add(ExportScope.Cuts)
    }

    /** The user's choice while it still exists, otherwise the most specific work they did: the cuts, then the A-B range. */
    fun resolve(chosen: ExportScope?, hasLoop: Boolean, hasCuts: Boolean): ExportScope {
        val offered = available(hasLoop, hasCuts)
        return when {
            chosen != null && chosen in offered -> chosen
            hasCuts -> ExportScope.Cuts
            hasLoop -> ExportScope.Loop
            else -> ExportScope.Whole
        }
    }
}
