package com.iphonecam.sender

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Matrix
import android.graphics.SurfaceTexture
import android.net.wifi.WifiManager
import android.os.Bundle
import android.view.TextureView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.iphonecam.sender.core.QualityTiers
import com.iphonecam.sender.ui.theme.*
import kotlin.math.max

class MainActivity : ComponentActivity() {

    private val vm: SenderViewModel by viewModels()
    private var wifiLock: WifiManager.WifiLock? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            IPhoneCamTheme {
                val state by vm.state.collectAsState()
                LaunchedEffect(state.broadcasting) { applyKeepAlive(state.broadcasting) }
                AppRoot(state, vm)
            }
        }
    }

    private fun applyKeepAlive(on: Boolean) {
        if (on) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            try {
                val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "iPhoneCam:stream")
                wifiLock?.acquire()
            } catch (_: Exception) {}
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            try { if (wifiLock?.isHeld == true) wifiLock?.release() } catch (_: Exception) {}
            wifiLock = null
        }
    }

    override fun onDestroy() {
        applyKeepAlive(false)
        super.onDestroy()
    }
}

private enum class Page { Home, Settings, Help }

@Composable
private fun AppRoot(state: UiState, vm: SenderViewModel) {
    var page by remember { mutableStateOf(Page.Home) }
    val ctx = LocalContext.current
    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }

    LaunchedEffect(Unit) {
        if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED
        ) permissionLauncher.launch(Manifest.permission.CAMERA)
    }

    Box(
        Modifier
            .fillMaxSize()
            .background(Bg)
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .windowInsetsPadding(WindowInsets.systemBars)
        ) {
            TopBar(state)
            Box(Modifier.weight(1f)) {
                when (page) {
                    Page.Home -> HomePage(state, vm)
                    Page.Settings -> SettingsPage(state, vm)
                    Page.Help -> HelpPage()
                }
            }
            BottomNav(page) { page = it }
        }
    }
}

// ==================== 共用元件 ====================

private val NeonGradient = Brush.linearGradient(listOf(Cyan, Purple, Pink))

@Composable
private fun NeonCard(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    Surface(
        color = Panel,
        shape = RoundedCornerShape(16.dp),
        border = androidx.compose.foundation.BorderStroke(1.dp, Line),
        modifier = modifier
    ) {
        Column(Modifier.padding(14.dp), content = content)
    }
}

@Composable
private fun TopBar(state: UiState) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 18.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text("🐰", fontSize = 22.sp)
        Spacer(Modifier.width(8.dp))
        Text("泡泡直播助手", color = Pink, fontSize = 19.sp, fontWeight = FontWeight.Bold)
        Text("· 手機端", color = Muted, fontSize = 12.sp, modifier = Modifier.padding(start = 6.dp))
        Spacer(Modifier.weight(1f))
        Box(
            Modifier
                .clip(RoundedCornerShape(10.dp))
                .background(PanelHi)
                .padding(horizontal = 10.dp, vertical = 4.dp)
        ) {
            Text(
                if (state.connected) "● 已連接 PC" else "○ 等待連線",
                color = if (state.connected) Success else Muted,
                fontSize = 12.sp
            )
        }
    }
}

@Composable
private fun BottomNav(page: Page, onSelect: (Page) -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        NavItem(Modifier.weight(1f), "首頁", Icons.Filled.Home, page == Page.Home) { onSelect(Page.Home) }
        NavItem(Modifier.weight(1f), "設定", Icons.Filled.Settings, page == Page.Settings) { onSelect(Page.Settings) }
        NavItem(Modifier.weight(1f), "幫助", Icons.Filled.Info, page == Page.Help) { onSelect(Page.Help) }
    }
}

@Composable
private fun NavItem(mod: Modifier, label: String, icon: ImageVector, selected: Boolean, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    Column(
        mod
            .heightIn(min = 52.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(if (selected) PanelHi else Color.Transparent)
            .clickable(interactionSource = interaction, indication = null, onClick = onClick)
            .pressScale(interaction)
            .padding(vertical = 8.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Icon(icon, contentDescription = label,
            tint = if (selected) Pink else Muted, modifier = Modifier.size(22.dp))
        Spacer(Modifier.height(3.dp))
        Text(label, color = if (selected) Pink else Muted, fontSize = 12.sp)
    }
}

/** 按下時輕微縮小：給所有可點元素一致、低干擾的觸覺回饋（UIUX：回饋快、不位移版面）。 */
@Composable
private fun Modifier.pressScale(interactionSource: MutableInteractionSource): Modifier {
    val pressed by interactionSource.collectIsPressedAsState()
    val scale by animateFloatAsState(targetValue = if (pressed) 0.97f else 1f, label = "pressScale")
    return this.graphicsLayer { scaleX = scale; scaleY = scale }
}

// ==================== 首頁 ====================
@Composable
private fun HomePage(state: UiState, vm: SenderViewModel) {
    Column(
        Modifier
            .fillMaxSize()
            .padding(horizontal = 14.dp, vertical = 8.dp)
    ) {
        // 預覽（自適應各種螢幕比例，佔滿剩餘高度；資訊以 overlay 疊在畫面上，不再佔額外高度）
        BoxWithConstraints(
            Modifier
                .fillMaxWidth()
                .weight(1f),
            contentAlignment = Alignment.Center
        ) {
            val aspect = if (state.bufW > 0 && state.bufH > 0)
                minOf(state.bufW, state.bufH).toFloat() / maxOf(state.bufW, state.bufH).toFloat()
            else 0.75f
            val useHeight = maxWidth / maxHeight > aspect
            val w = if (useHeight) maxHeight * aspect else maxWidth
            val h = if (useHeight) maxHeight else maxWidth / aspect

            Box(
                Modifier
                    .size(width = w, height = h)
                    .clip(RoundedCornerShape(12.dp))
                    .background(Color(0xFF05050F))
                    .border(1.dp, Cyan, RoundedCornerShape(12.dp))
            ) {
                PreviewView(state, onSurface = { vm.attachPreview(it) }, modifier = Modifier.fillMaxSize())
                if (!state.hasVideo) {
                    StandbyView(Modifier.fillMaxSize())
                }
                // 上方 overlay：LIVE + 解析度／幀數
                Row(
                    Modifier.align(Alignment.TopStart).padding(10.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    if (state.broadcasting && state.hasVideo) {
                        Box(
                            Modifier
                                .clip(RoundedCornerShape(6.dp))
                                .background(NeonGradient)
                                .padding(horizontal = 9.dp, vertical = 3.dp)
                        ) { Text("● LIVE", color = Color.White, fontSize = 12.sp, fontWeight = FontWeight.Bold) }
                        Spacer(Modifier.width(8.dp))
                    }
                    Box(
                        Modifier
                            .clip(RoundedCornerShape(6.dp))
                            .background(Color(0x990D0D24))
                            .padding(horizontal = 8.dp, vertical = 3.dp)
                    ) {
                        Text(
                            "${QualityTiers.names[state.resIdx]} ${QualityTiers.fpsOptions[state.fpsIdx]}FPS",
                            color = Muted, fontSize = 12.sp, fontFamily = MonoFont
                        )
                    }
                }
                // 下方 overlay：系統資訊（連線狀態＋訊息）
                Column(Modifier.align(Alignment.BottomStart).padding(10.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(
                            Modifier.size(8.dp).clip(RoundedCornerShape(4.dp))
                                .background(if (state.connected) Success else MutedDim)
                        )
                        Spacer(Modifier.width(6.dp))
                        Text(
                            if (state.connected) "已連接 PC · ${state.clientIp}" else "等待 PC 連接…",
                            color = Color.White, fontSize = 12.sp, fontFamily = MonoFont
                        )
                    }
                    Text(
                        state.status, color = Muted, fontSize = 11.sp, fontFamily = MonoFont,
                        maxLines = 1, overflow = TextOverflow.Ellipsis
                    )
                }
            }
        }

        Spacer(Modifier.height(10.dp))

        val goInteraction = remember { MutableInteractionSource() }
        Button(
            onClick = { vm.toggle() },
            modifier = Modifier.fillMaxWidth().height(52.dp).pressScale(goInteraction),
            interactionSource = goInteraction,
            shape = RoundedCornerShape(14.dp),
            colors = ButtonDefaults.buttonColors(containerColor = Color.Transparent),
            contentPadding = PaddingValues()
        ) {
            Box(
                Modifier
                    .fillMaxSize()
                    .background(if (state.broadcasting) Brush.linearGradient(listOf(PanelHi, PanelHi)) else NeonGradient),
                contentAlignment = Alignment.Center
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(
                        if (state.broadcasting) Icons.Filled.Stop else Icons.Filled.PlayArrow,
                        contentDescription = null,
                        tint = if (state.broadcasting) Muted else Color.White
                    )
                    Spacer(Modifier.width(8.dp))
                    Text(
                        if (state.broadcasting) "停止直播" else "開始直播",
                        color = if (state.broadcasting) Muted else Color.White,
                        fontSize = 16.sp, fontWeight = FontWeight.Bold
                    )
                }
            }
        }
        Spacer(Modifier.height(8.dp))
    }
}

/** 無串流時的待機畫面：霓虹光環＋相機圖示（呼吸動畫）＋提示文字。 */
@Composable
private fun StandbyView(modifier: Modifier) {
    val transition = rememberInfiniteTransition(label = "standby")
    val pulse by transition.animateFloat(
        initialValue = 0.9f, targetValue = 1.06f,
        animationSpec = infiniteRepeatable(tween(1400), RepeatMode.Reverse), label = "pulse"
    )
    Box(
        modifier.background(Brush.verticalGradient(listOf(Color(0xFF0D0D24), Color(0xFF1A1A35)))),
        contentAlignment = Alignment.Center
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Box(
                Modifier
                    .size(104.dp)
                    .graphicsLayer { scaleX = pulse; scaleY = pulse }
                    .clip(CircleShape)
                    .background(Color(0x1A35D6FF))
                    .border(2.dp, NeonGradient, CircleShape),
                contentAlignment = Alignment.Center
            ) {
                Icon(Icons.Filled.Videocam, contentDescription = null, tint = Cyan, modifier = Modifier.size(44.dp))
            }
            Spacer(Modifier.height(18.dp))
            Text("等待手機連線", color = TextPrimary, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(6.dp))
            Text("在手機端按「開始直播」", color = Muted, fontSize = 12.sp)
        }
    }
}

@Composable
private fun FieldRow(label: String, value: String, icon: ImageVector, enabled: Boolean = true, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp)
            .clip(RoundedCornerShape(8.dp))
            .clickable(enabled = enabled, interactionSource = interaction, indication = null, onClick = onClick)
            .pressScale(interaction)
            .padding(horizontal = 4.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(icon, contentDescription = null, tint = if (enabled) Cyan else MutedDim, modifier = Modifier.size(20.dp))
        Spacer(Modifier.width(12.dp))
        Text(label, color = if (enabled) TextPrimary else Muted, fontSize = 14.sp, modifier = Modifier.weight(1f))
        Text(if (enabled) value else "直播中鎖定", color = Muted, fontSize = 13.sp)
    }
}

// ==================== 設定 / 幫助 ====================

@Composable
private fun SettingsPage(state: UiState, vm: SenderViewModel) {
    Column(
        Modifier
            .fillMaxSize()
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        NeonCard(Modifier.fillMaxWidth()) {
            Text("輸出設定", color = Pink, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
            FieldRow("輸出解析度", "${QualityTiers.names[state.resIdx]}  ＞", Icons.Filled.AspectRatio,
                enabled = true) { vm.cycleRes() }
            FieldRow("輸出 FPS", "${QualityTiers.fpsOptions[state.fpsIdx]}FPS  ＞", Icons.Filled.Speed,
                enabled = true) { vm.cycleFps() }
            // 鏡頭切換：列出所有實際鏡頭；串流中鎖定
            val currentLens = state.lenses.firstOrNull { it.id == state.selectedLensId }?.label ?: "鏡頭"
            var lensMenu by remember { mutableStateOf(false) }
            Box {
                FieldRow("鏡頭切換", "$currentLens  ＞", Icons.Filled.Cameraswitch,
                    enabled = !state.broadcasting) { lensMenu = true }
                DropdownMenu(
                    expanded = lensMenu,
                    onDismissRequest = { lensMenu = false },
                    modifier = Modifier.background(Panel)
                ) {
                    state.lenses.forEach { lens ->
                        DropdownMenuItem(
                            enabled = lens.enabled,
                            text = {
                                Text(
                                    lens.label + if (lens.focal > 0f) "  ·  %.0fmm".format(lens.focal) else "",
                                    color = when {
                                        !lens.enabled -> MutedDim
                                        lens.id == state.selectedLensId -> Pink
                                        else -> TextPrimary
                                    }
                                )
                            },
                            onClick = { if (lens.enabled) { vm.selectLens(lens.id); lensMenu = false } }
                        )
                    }
                }
            }
        }

        NeonCard(Modifier.fillMaxWidth()) {
            Text("碼率（Mbps，改完立即重編）", color = TextPrimary, fontSize = 14.sp)
            Spacer(Modifier.height(6.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Slider(
                    value = state.mbps.toFloat(),
                    onValueChange = { vm.setBitrate(it.toDouble()) },
                    valueRange = 1f..40f,
                    modifier = Modifier.weight(1f),
                    colors = SliderDefaults.colors(
                        thumbColor = Pink, activeTrackColor = Cyan, inactiveTrackColor = Line,
                        activeTickColor = Color.Transparent, inactiveTickColor = Color.Transparent
                    )
                )
                Text("${state.mbps.toInt()}M", color = TextPrimary, fontSize = 14.sp, fontFamily = MonoFont,
                    modifier = Modifier.width(50.dp))
            }
        }

        Text("版本 v2.0 · CAM1", color = Muted, fontSize = 12.sp, modifier = Modifier.padding(start = 4.dp))
    }
}

@Composable
private fun HelpPage() {
    Column(
        Modifier
            .fillMaxSize()
            .padding(horizontal = 14.dp, vertical = 8.dp)
    ) {
        NeonCard(Modifier.fillMaxWidth()) {
            Text("使用方式", color = Pink, fontSize = 15.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(6.dp))
            Text(
                "1. 手機與電腦連同一個 Wi-Fi\n" +
                "2. 按「開始直播」（螢幕保持亮著）\n" +
                "3. 電腦端按「連線」，畫面就進來\n" +
                "4. OBS／Discord 選 iPhoneCam 即可播出",
                color = Muted, fontSize = 13.sp, lineHeight = 24.sp
            )
        }
    }
}

// ==================== 預覽（依感測器固定方向，cover 縮放） ====================

/**
 * 把相機 buffer（srcW×srcH，感測器原生橫向）以固定 rotation 旋轉成正立，
 * 再 cover 縮放置中填滿檢視框。rotation 只依感測器、不依裝置姿態，故手機旋轉時畫面方向固定。
 * 覆寫 onSizeChanged 與每次來源/旋轉變更都重算，避免換檔後矩陣未更新而縮到角落。
 */
private class CameraPreviewView(ctx: Context) : TextureView(ctx) {
    private var srcW = 0
    private var srcH = 0
    private var rotation = 90
    private var mirror = false

    fun configure(w: Int, h: Int, rot: Int, mir: Boolean) {
        if (w == srcW && h == srcH && rot == rotation && mir == mirror) return
        srcW = w; srcH = h; rotation = rot; mirror = mir
        applyTransform()
    }

    private fun applyTransform() {
        if (width <= 0 || height <= 0 || srcW <= 0 || srcH <= 0) return
        val vw = width.toFloat()
        val vh = height.toFloat()
        val bw = srcW.toFloat()
        val bh = srcH.toFloat()
        val rotated = rotation % 180 != 0
        val ew = if (rotated) bh else bw // 旋轉後佔用的寬
        val eh = if (rotated) bw else bh // 旋轉後佔用的高
        val s = max(vw / ew, vh / eh)    // cover

        // 以「buffer 座標」為基準的目標矩陣：置中、鏡像、旋轉、cover、再置中
        val m = Matrix()
        m.setTranslate(-bw / 2f, -bh / 2f)
        if (mirror) m.postScale(-1f, 1f)
        m.postRotate(rotation.toFloat())
        m.postScale(s, s)
        m.postTranslate(vw / 2f, vh / 2f)

        // TextureView 預設會把 buffer 拉伸填滿檢視框；先抵消這個拉伸，矩陣才會以 buffer 座標正確作用
        val stretch = Matrix()
        stretch.setScale(vw / bw, vh / bh)
        val inv = Matrix()
        if (stretch.invert(inv)) m.preConcat(inv)
        setTransform(m)
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        applyTransform()
    }
}

@Composable
private fun PreviewView(state: UiState, onSurface: (SurfaceTexture) -> Unit, modifier: Modifier) {
    // 預覽來源依裝置方向（手機直拿 → 直向）：實際寬高與 encoder 尺寸相反
    val bufW = if (state.bufH > 0) state.bufH else QualityTiers.h()
    val bufH = if (state.bufW > 0) state.bufW else QualityTiers.w()

    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            CameraPreviewView(ctx).apply {
                surfaceTextureListener = object : TextureView.SurfaceTextureListener {
                    override fun onSurfaceTextureAvailable(s: SurfaceTexture, w: Int, h: Int) = onSurface(s)
                    override fun onSurfaceTextureSizeChanged(s: SurfaceTexture, w: Int, h: Int) {}
                    override fun onSurfaceTextureDestroyed(s: SurfaceTexture) = true
                    override fun onSurfaceTextureUpdated(s: SurfaceTexture) {}
                }
            }
        },
        update = { tv -> tv.configure(bufW, bufH, state.previewRot, state.previewMirror) }
    )
}
