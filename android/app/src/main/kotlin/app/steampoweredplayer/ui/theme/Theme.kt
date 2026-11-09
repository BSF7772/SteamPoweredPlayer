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

/** The brand's blue, lightened so text and icons in it stay readable on the dark surfaces. */
private val DarkPrimary = Color(0xFFA9C1FF)

private val Dark = darkColorScheme(
    primary = DarkPrimary, onPrimary = Color(0xFF10204A), primaryContainer = Color(0xFF24345F), onPrimaryContainer = Color(0xFFDCE5FF),
    secondary = DarkPrimary, onSecondary = Color(0xFF10204A), secondaryContainer = Color(0xFF24345F), onSecondaryContainer = Color(0xFFDCE5FF),
    tertiary = Brand.Cyan, onTertiary = Color.White,
    background = Color(0xFF101018), onBackground = Color(0xFFF2F2F7),
    surface = Color(0xFF101018), onSurface = Color(0xFFF2F2F7),
    surfaceContainerLow = Color(0xFF16161F), surfaceContainer = Color(0xFF1B1B26), surfaceContainerHigh = Color(0xFF20202C), surfaceContainerHighest = Color(0xFF262633),
    surfaceVariant = Color(0xFF262633), onSurfaceVariant = Color(0xFFB9B9C7),
    outline = Color(0xFF8A8A9A), outlineVariant = Color(0xFF5A5A6E),
    error = Color(0xFFFFB4AB), onError = Color(0xFF4A2024), errorContainer = Color(0xFF4A2024), onErrorContainer = Color(0xFFFFDAD6),
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
