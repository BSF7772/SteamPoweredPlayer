// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer

import java.io.File

/** The golden files the C# tests use too (tests/fixtures/golden). */
object Golden {
    private val dir = File(requireNotNull(System.getProperty("spp.golden")) { "spp.golden is set by app/build.gradle.kts" })
    fun bytes(name: String): ByteArray = File(dir, name).readBytes().filter { it != '\r'.code.toByte() }.toByteArray()
    fun text(name: String): String = bytes(name).toString(Charsets.UTF_8)

    /** Exactly the file's bytes, for binary fixtures. [name] may lead out of the golden folder (`../pb/…`). */
    fun raw(name: String): ByteArray = File(dir, name).readBytes()
}
