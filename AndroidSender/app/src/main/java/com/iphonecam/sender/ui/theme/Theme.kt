package com.iphonecam.sender.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily

// 深紫黑 + 霓虹青粉（致敬《主播女孩重度依賴》NSO 風）
val Bg = Color(0xFF0D0D24)
val Panel = Color(0xFF12122A)
val PanelHi = Color(0xFF1A1A35)
val Line = Color(0xFF2A2A4A)
val TextPrimary = Color(0xFFFFFFFF)
val Muted = Color(0xFF9AA3C0)
val MutedDim = Color(0xFF555577)
val Cyan = Color(0xFF35D6FF)
val Pink = Color(0xFFFF5FD2)
val Purple = Color(0xFF8B5CF6)
val Success = Color(0xFF3DFF88)
val Danger = Color(0xFFFF4D6D)

val MonoFont = FontFamily.Monospace

private val Scheme = darkColorScheme(
    primary = Cyan,
    onPrimary = Color(0xFF06202B),
    secondary = Pink,
    onSecondary = Color.White,
    tertiary = Purple,
    background = Bg,
    onBackground = TextPrimary,
    surface = Panel,
    onSurface = TextPrimary,
    surfaceVariant = PanelHi,
    onSurfaceVariant = Muted,
    outline = Line,
    error = Danger,
    onError = Color.White,
)

@Composable
fun IPhoneCamTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = Scheme, typography = Typography(), content = content)
}
