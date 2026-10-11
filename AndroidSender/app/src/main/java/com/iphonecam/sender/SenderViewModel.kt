package com.iphonecam.sender

import android.app.Application
import android.graphics.SurfaceTexture
import android.os.Build
import android.os.PowerManager
import android.util.Base64
import androidx.lifecycle.AndroidViewModel
import com.iphonecam.sender.core.NetServer
import com.iphonecam.sender.core.QualityTiers
import com.iphonecam.sender.core.StreamEngine
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import org.json.JSONObject

data class UiState(
    val broadcasting: Boolean = false,
    val hasVideo: Boolean = false,
    val connected: Boolean = false,
    val clientIp: String = "-",
    val resIdx: Int = 0,
    val fpsIdx: Int = 0,
    val mbps: Double = 4.0,
    val useFront: Boolean = false,
    val selectedLensId: String = "",
    val lenses: List<StreamEngine.LensInfo> = emptyList(),
    val isHevc: Boolean = false,
    val bufW: Int = 960,
    val bufH: Int = 720,
    val previewRot: Int = 90,
    val previewMirror: Boolean = false,
    val status: String = "待機",
)

class SenderViewModel(app: Application) : AndroidViewModel(app) {

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state.asStateFlow()

    private var thermalListener: PowerManager.OnThermalStatusChangedListener? = null

    private val server: NetServer = NetServer(object : NetServer.Callbacks {
        override fun log(msg: String) = setStatus(msg)
        override fun onSetVideo(width: Int, height: Int, fps: Int, mbps: Double, codec: String) {
            QualityTiers.snapTo(width, height, fps)
            engine.applyQuality(width, height, fps, if (mbps > 0) mbps else QualityTiers.mbps(), codec)
            pushQualityState()
        }
        override fun onSetImage(json: JSONObject) {
            setStatus("PC 美顏參數已收下（未啟用）")
        }
        override fun onIdrRequest() = engine.requestIdr()
        override fun onNetState(loss: Double, jitterMs: Int) = engine.onNetState(loss, jitterMs)
        override fun onConnChanged(connected: Boolean, ip: String) {
            _state.update { it.copy(connected = connected, clientIp = if (connected) ip else "-") }
            if (connected) { sendVideoParams(); sendCodecConfig() }
        }
    })

    private val engine: StreamEngine = StreamEngine(app, object : StreamEngine.Callbacks {
        override fun log(msg: String) = setStatus(msg)
        override fun frame(avcc: ByteArray, key: Boolean, hevc: Boolean) = server.sendFrame(avcc, key, hevc)
        override fun csd(seqHead: ByteArray, pps: ByteArray, hevc: Boolean) {
            val o = JSONObject()
            o.put("sps", Base64.encodeToString(seqHead, Base64.NO_WRAP))
            o.put("pps", Base64.encodeToString(pps, Base64.NO_WRAP))
            server.sendControl("codec_config", o)
        }
        override fun videoParams(p: StreamEngine.VideoParams) {
            _state.update { it.copy(isHevc = p.codec == "hevc", bufW = p.w, bufH = p.h) }
            server.beaconMax = QualityTiers.names[QualityTiers.resIdx] + QualityTiers.fpsOptions[QualityTiers.fpsIdx]
            sendVideoParams(p)
        }
        override fun orientationChanged(rot: Int, flip: Int) {
            server.sendControl("orientation", JSONObject().apply { put("rot", rot); put("flip", flip) })
        }
        override fun rejected(reason: String, fallback: String) {
            setStatus("編碼被拒（$reason），回退 $fallback")
            server.sendControl("video_rejected", JSONObject().apply { put("reason", reason); put("fallback", fallback) })
        }
        override fun hasVideoChanged(v: Boolean) = _state.update { it.copy(hasVideo = v) }
        override fun onPreviewConfig(rotationDegrees: Int, mirror: Boolean) {
            _state.update { it.copy(previewRot = rotationDegrees, previewMirror = mirror) }
        }
    })

    init {
        server.beaconHevc = hevcSupported()
        _state.update { it.copy(lenses = engine.lenses, selectedLensId = engine.selectedLensId ?: "") }
    }

    fun attachPreview(st: SurfaceTexture) = engine.attachPreview(st)

    // ---------------- 直播開關 ----------------

    fun toggle() {
        if (_state.value.broadcasting) stopBroadcast() else startBroadcast()
    }

    private fun startBroadcast() {
        QualityTiers.resIdx = _state.value.resIdx
        QualityTiers.fpsIdx = _state.value.fpsIdx
        engine.applyQuality(QualityTiers.w(), QualityTiers.h(), QualityTiers.fps(), QualityTiers.mbps(), QualityTiers.codecFor())
        engine.startStream()
        server.start()
        startThermalWatch()
        _state.update { it.copy(broadcasting = true) }
        setStatus("LIVE 廣播中…")
    }

    private fun stopBroadcast() {
        engine.stopStream()
        server.stop()
        stopThermalWatch()
        _state.update { it.copy(broadcasting = false, hasVideo = false, connected = false) }
        setStatus("已停止")
    }

    // ---------------- 檔位 / 鏡頭 / 碼率 ----------------

    fun cycleRes() {
        QualityTiers.resIdx = (QualityTiers.resIdx + 1) % QualityTiers.resolutions.size
        val br = QualityTiers.mbps()
        _state.update { it.copy(resIdx = QualityTiers.resIdx, mbps = br) }
        // 廣播中也可即時換檔（引擎走穩健重建）
        engine.applyQuality(QualityTiers.w(), QualityTiers.h(), QualityTiers.fps(), br, QualityTiers.codecFor())
    }

    fun cycleFps() {
        QualityTiers.fpsIdx = (QualityTiers.fpsIdx + 1) % QualityTiers.fpsOptions.size
        _state.update { it.copy(fpsIdx = QualityTiers.fpsIdx) }
        engine.applyQuality(QualityTiers.w(), QualityTiers.h(), QualityTiers.fps(), QualityTiers.mbps(), QualityTiers.codecFor())
    }

    fun switchCamera() {
        engine.switchCamera()
        _state.update { it.copy(useFront = engine.useFront, selectedLensId = engine.selectedLensId ?: "") }
    }

    fun selectLens(id: String) {
        engine.selectLens(id)
        _state.update { it.copy(useFront = engine.useFront, selectedLensId = id) }
    }

    fun setBitrate(mbps: Double) {
        _state.update { it.copy(mbps = mbps) }
        engine.setBitrate(mbps)
    }

    private fun pushQualityState() {
        _state.update { it.copy(resIdx = QualityTiers.resIdx, fpsIdx = QualityTiers.fpsIdx, mbps = QualityTiers.mbps()) }
    }

    // ---------------- 訊息 ----------------

    private fun sendVideoParams(p: StreamEngine.VideoParams = StreamEngine.VideoParams(
        engine.vidW, engine.vidH, engine.vidFps,
        if (engine.isHevc) "hevc" else "h264", 0, 0
    )) {
        val o = JSONObject()
        o.put("width", p.w); o.put("height", p.h); o.put("fps", p.fps); o.put("codec", p.codec)
        o.put("rot", p.rot); o.put("flip", p.flip)
        server.sendControl("video_params", o)
    }

    private fun sendCodecConfig() {
        val head = engine.seqHeadBytes ?: return
        val pps = engine.ppsBytes ?: return
        val o = JSONObject()
        o.put("sps", Base64.encodeToString(head, Base64.NO_WRAP))
        o.put("pps", Base64.encodeToString(pps, Base64.NO_WRAP))
        server.sendControl("codec_config", o)
    }

    private fun setStatus(msg: String) { _state.update { it.copy(status = msg) } }

    private fun hevcSupported(): Boolean = try {
        val c = android.media.MediaCodec.createEncoderByType("video/hevc"); c.release(); true
    } catch (_: Exception) { false }

    // ---------------- 過熱保護 ----------------

    private fun startThermalWatch() {
        if (Build.VERSION.SDK_INT < 29) return
        val pm = getApplication<Application>().getSystemService(PowerManager::class.java) ?: return
        stopThermalWatch()
        val l = PowerManager.OnThermalStatusChangedListener { status ->
            if (status >= 4 && _state.value.mbps > 2.0) {
                setBitrate(2.0)
                setStatus("過熱降碼率到 2Mbps（要全速請重開廣播）")
            }
        }
        thermalListener = l
        pm.addThermalStatusListener(getApplication<Application>().mainExecutor, l)
    }

    private fun stopThermalWatch() {
        if (Build.VERSION.SDK_INT < 29) return
        val l = thermalListener ?: return
        val pm = getApplication<Application>().getSystemService(PowerManager::class.java) ?: return
        try { pm.removeThermalStatusListener(l) } catch (_: Exception) {}
        thermalListener = null
    }

    override fun onCleared() {
        engine.release()
        server.stop()
        stopThermalWatch()
        super.onCleared()
    }
}
