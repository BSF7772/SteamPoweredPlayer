// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp

/** The desktop's palette (src/Themes/Brand.xaml), which follows FastEncoder. */
object Brand {
    val Purple = Color(0xFF7C3AED)
    val Blue = Color(0xFF2563EB)
    val Cyan = Color(0xFF06B6D4)
    val Live = Color(0xFFEF4444)
    private val stops = arrayOf(0f to Purple, 0.55f to Blue, 1f to Cyan)

    /** Diagonal, for heroes and tiles. */
    val Gradient = Brush.linearGradient(*stops, start = Offset.Zero, end = Offset.Infinite)

    /** Horizontal, for bars and strips. */
    val GradientH = Brush.horizontalGradient(*stops)
}

private val Dark = darkColorScheme(
    primary = Brand.Blue, secondary = Brand.Purple, tertiary = Brand.Cyan,
    onPrimary = Color.White, onSecondary = Color.White, onTertiary = Color.White,
    background = Color(0xFF101018), onBackground = Color(0xFFF2F2F7),
    surface = Color(0xFF101018), onSurface = Color(0xFFF2F2F7),
    surfaceContainer = Color(0xFF1B1B26), surfaceVariant = Color(0xFF262633), onSurfaceVariant = Color(0xFFB9B9C7),
)

private val Light = lightColorScheme(
    primary = Brand.Blue, secondary = Brand.Purple, tertiary = Brand.Cyan,
    onPrimary = Color.White, onSecondary = Color.White, onTertiary = Color.White,
)

private val CardShapes = Shapes(
    small = RoundedCornerShape(8.dp), medium = RoundedCornerShape(12.dp), large = RoundedCornerShape(16.dp),
)

@Composable
fun SppTheme(dark: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) =
    MaterialTheme(colorScheme = if (dark) Dark else Light, shapes = CardShapes, content = content)
