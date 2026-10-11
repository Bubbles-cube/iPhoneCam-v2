package com.iphonecam.sender.core

import android.content.Context
import android.graphics.ImageFormat
import android.graphics.SurfaceTexture
import android.hardware.SensorManager
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.ImageReader
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import android.util.Range
import android.util.Size
import android.view.OrientationEventListener
import android.view.Surface
import java.nio.ByteBuffer
import kotlin.math.abs
import kotlin.math.sqrt

/**
 * 串流引擎（SPEC-v4 §11.2）：Camera2 擷取 + MediaCodec 硬編（H.264/HEVC）+ 方向感測。
 * 串流維持 sensor 原生方向，交由 PC 用 ffmpeg -vf 轉正；本機預覽由 UI 端以 TextureView transform 轉正。
 * 手機是執行者，檔位／碼率由 PC（或本機 UI）決定。
 */
class StreamEngine(private val ctx: Context, private val cb: Callbacks) {

    interface Callbacks {
        fun log(msg: String)
        fun frame(avcc: ByteArray, key: Boolean, hevc: Boolean)
        fun csd(seqHead: ByteArray, pps: ByteArray, hevc: Boolean)
        fun videoParams(p: VideoParams)
        fun orientationChanged(rot: Int, flip: Int)
        fun rejected(reason: String, fallback: String)
        fun hasVideoChanged(v: Boolean)
        fun onPreviewConfig(rotationDegrees: Int, mirror: Boolean)
    }

    data class VideoParams(val w: Int, val h: Int, val fps: Int, val codec: String, val rot: Int, val flip: Int)

    companion object { private const val TAG = "IPC-Engine" }

    private val bgThread = HandlerThread("cam").also { it.start() }
    private val bg = Handler(bgThread.looper)

    /** 把會動到 Camera2／MediaCodec 的工作丟到相機執行緒序列執行，並吞掉例外，避免 UI 執行緒直接操作導致崩潰。 */
    private fun bgSafe(block: () -> Unit) {
        bg.post { try { block() } catch (e: Exception) { cb.log("內部錯誤：${e.message}") } }
    }
    private val cameras = ctx.getSystemService(Context.CAMERA_SERVICE) as CameraManager

    data class LensInfo(val id: String, val facing: Int, val focal: Float, val label: String, val enabled: Boolean = true)

    private val _lenses = ArrayList<LensInfo>()
    val lenses: List<LensInfo> get() = _lenses

    /** 目前選用的鏡頭 id（Samsung 把各鏡頭各自暴露為獨立 id，可直接 openCamera）。 */
    @Volatile var selectedLensId: String? = null; private set

    /** 目前是否為前置鏡頭（依選用鏡頭的 facing）。 */
    val useFront: Boolean
        get() = _lenses.firstOrNull { it.id == selectedLensId }?.facing == CameraCharacteristics.LENS_FACING_FRONT

    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reqBuilder: CaptureRequest.Builder? = null
    @Volatile private var currentZoom = 1.0f
    @Volatile private var encoder: MediaCodec? = null
    private var encoderSurface: Surface? = null
    private var st: SurfaceTexture? = null
    private var previewSurface: Surface? = null

    @Volatile var streaming = false; private set
    @Volatile var hasVideo = false; private set

    private var drainThread: Thread? = null
    @Volatile private var encGen = 0
    private var encRestartTries = 0

    var vidW = 960; private set
    var vidH = 720; private set
    var vidFps = 30; private set
    var vidCodec = "video/avc"; private set
    var vidMbps = 4.0; private set

    var bufferW = 960; private set
    var bufferH = 720; private set

    private var sps: ByteArray? = null
    private var pps: ByteArray? = null
    private var vps: ByteArray? = null
    private var spsLogged = false

    private var sensorOrient = 90
    private var overReader: ImageReader? = null
    private var overSurface: Surface? = null
    private var overW = 0; private var overH = 0
    private var overOff = false
    private var sessFails = 0
    private var overSizes: MutableList<IntArray>? = null
    private var fpsRanges: Array<Range<Int>>? = null

    private var lastAngle = -1; private var curRot = 0; private var curFlip = 0
    private var rotCandidate = -1; private var rotCandidateSince = 0L
    @Volatile private var lastOrientMs = 0L

    var abrBase = 4.0
    var abrStep = 0
    private var abrGoodStreak = 0

    private val orient = object : OrientationEventListener(ctx, SensorManager.SENSOR_DELAY_NORMAL) {
        override fun onOrientationChanged(o: Int) {
            if (o == ORIENTATION_UNKNOWN) return
            val a = ((o + 45) / 90 * 90) % 360
            bg.post { debounceAngle(a) }
        }
    }

    init {
        enumerateLenses()
        if (orient.canDetectOrientation()) orient.enable()
    }

    /**
     * 列舉所有可開啟的相機（Samsung 把各鏡頭各自暴露為獨立 id），
     * 依 LENS_FACING 與焦距分類成超廣角／主鏡／長焦／前置，作為鏡頭切換清單。
     * S21 後置：13mm 超廣角、26mm 主鏡、29mm 長焦；前置 26mm。
     */
    private fun enumerateLenses() {
        val raw = ArrayList<LensInfo>()
        val diag = StringBuilder()
        // 逐顆列舉：單一 id 讀取失敗不再中斷整份清單（否則會漏掉後面的鏡頭）
        for (id in cameras.cameraIdList) {
            try {
                val cc = cameras.getCameraCharacteristics(id)
                val facing = cc.get(CameraCharacteristics.LENS_FACING) ?: -1
                val fl = cc.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)
                val rawFocal = if (fl != null && fl.isNotEmpty()) fl[0] else 0f
                val focal = equivalentFocal(rawFocal, cc)
                val logical = cc.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES)
                    ?.contains(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES_LOGICAL_MULTI_CAMERA) == true
                diag.append("id$id/${facingName(facing)}/").append("%.0f".format(focal)).append("mm")
                if (logical) diag.append("/L")
                diag.append("  ")
                raw.add(LensInfo(id, facing, focal, ""))
            } catch (e: Exception) {
                diag.append("id$id/ERR(${e.javaClass.simpleName})  ")
            }
        }

        _lenses.clear()
        // 先查機型對照表（含原廠鎖住的鏡頭）；未收錄機型才由實際列舉推斷
        val profile = DeviceCameraCatalog.forDevice()
        if (profile != null) buildFromProfile(profile, raw) else buildFromEnumeration(raw)

        if (selectedLensId == null || _lenses.none { it.id == selectedLensId && it.id.isNotEmpty() }) {
            selectedLensId = _lenses.firstOrNull {
                it.enabled && it.label == DeviceCameraCatalog.LensKind.WIDE.label
            }?.id ?: _lenses.firstOrNull { it.enabled }?.id
        }
        cb.log("相機清單 ${raw.size}：$diag")
    }

    /** 依機型對照表建立清單：可開的配對實際 id，鎖住的顯示為不可用。 */
    private fun buildFromProfile(profile: DeviceCameraCatalog.Profile, raw: List<LensInfo>) {
        val openBack = raw.filter { it.facing == CameraCharacteristics.LENS_FACING_BACK }
        val openFront = raw.filter { it.facing == CameraCharacteristics.LENS_FACING_FRONT }
            .sortedByDescending { it.focal }
        val used = HashSet<String>()
        for (exp in profile.lenses) {
            val isFront = exp.kind == DeviceCameraCatalog.LensKind.FRONT
            val pool = if (isFront) openFront else openBack
            val match = pool.filter { it.id !in used }.minByOrNull { abs(it.focal - exp.focal) }
            if (match != null && (match.focal <= 0f || abs(match.focal - exp.focal) <= 6f)) {
                used.add(match.id)
                _lenses.add(match.copy(label = exp.kind.label, enabled = true))
            } else {
                val facing = if (isFront) CameraCharacteristics.LENS_FACING_FRONT
                             else CameraCharacteristics.LENS_FACING_BACK
                val suffix = if (exp.locked) "（原廠未開放）" else "（不可用）"
                _lenses.add(LensInfo("", facing, exp.focal, exp.kind.label + suffix, enabled = false))
            }
        }
        // 對照表沒列到、但實際可開的後置鏡頭，補上（避免漏）
        for (r in raw) if (r.facing == CameraCharacteristics.LENS_FACING_BACK && r.id !in used)
            _lenses.add(r.copy(label = "後置鏡頭", enabled = true))
    }

    /** 未收錄機型：依焦距自動分類（超廣角／主鏡／長焦）。 */
    private fun buildFromEnumeration(raw: List<LensInfo>) {
        val back = raw.filter { it.facing == CameraCharacteristics.LENS_FACING_BACK }.sortedBy { it.focal }
        // 本機只有一顆前鏡；部分韌體會多暴露一顆輔助前鏡，取焦距最大的主前鏡即可
        val front = raw.filter { it.facing == CameraCharacteristics.LENS_FACING_FRONT }
            .sortedByDescending { it.focal }.take(1)
        back.forEachIndexed { i, l ->
            val label = when {
                back.size == 1 -> "後置鏡頭"
                i == 0 -> "後置超廣角"
                i == back.size - 1 && back.size >= 3 -> "後置長焦"
                else -> "後置主鏡"
            }
            _lenses.add(l.copy(label = label))
        }
        front.forEachIndexed { i, l ->
            _lenses.add(l.copy(label = if (front.size == 1) "前置鏡頭" else "前置鏡頭 ${i + 1}"))
        }
    }

    private fun facingName(f: Int): String = when (f) {
        CameraCharacteristics.LENS_FACING_FRONT -> "前"
        CameraCharacteristics.LENS_FACING_BACK -> "後"
        else -> "?"
    }

    /** 實體焦距 → 35mm 等效焦距（用感測器對角線換算，43.27mm 為 35mm 片幅對角線）。 */
    private fun equivalentFocal(rawFocal: Float, cc: CameraCharacteristics): Float {
        if (rawFocal <= 0f) return rawFocal
        return try {
            val phys = cc.get(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE)
            if (phys == null || phys.width <= 0f || phys.height <= 0f) rawFocal
            else {
                val diag = sqrt(phys.width * phys.width + phys.height * phys.height)
                if (diag <= 0f) rawFocal else rawFocal * 43.27f / diag
            }
        } catch (_: Exception) { rawFocal }
    }

    /** 切換鏡頭（重開相機；串流中亦走穩健重開）。 */
    fun selectLens(id: String) {
        if (id == selectedLensId || _lenses.none { it.id == id }) return
        selectedLensId = id
        currentZoom = 1.0f
        bgSafe {
            cb.log("切換鏡頭：${_lenses.firstOrNull { it.id == id }?.label ?: id}")
            closeSession()
            closeCamera()
            openCamera()
            updateRotation(true)
            cb.onPreviewConfig(previewRotationDegrees, previewMirror)
        }
    }

    fun attachPreview(s: SurfaceTexture) {
        st = s
        bgSafe {
            openCamera()
            cb.onPreviewConfig(previewRotationDegrees, previewMirror)
        }
    }

    val previewRotationDegrees: Int
        get() = 0
    val previewMirror: Boolean get() = false

    /** 目前相機支援的變焦範圍（Android 11+；null 表示不支援）。 */
    val zoomRange: ClosedFloatingPointRange<Float>?
        get() = try {
            if (Build.VERSION.SDK_INT < 30) null
            else {
                val id = selectedLensId
                if (id == null) null
                else cameras.getCameraCharacteristics(id)
                    .get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE)?.let { it.lower..it.upper }
            }
        } catch (_: Exception) { null }

    /** 設定變焦：不重建 session，直接重送 repeating request（邏輯鏡頭會據此切換實體鏡頭）。 */
    fun setZoom(ratio: Float) {
        currentZoom = ratio
        bg.post {
            try {
                if (Build.VERSION.SDK_INT < 30) return@post
                val req = reqBuilder ?: return@post
                val s = session ?: return@post
                req.set(CaptureRequest.CONTROL_ZOOM_RATIO, ratio)
                s.setRepeatingRequest(req.build(), null, bg)
            } catch (_: Exception) {}
        }
    }

    fun release() {
        try { orient.disable() } catch (_: Exception) {}
        bg.post {
            try {
                streaming = false
                stopEncoder()
                closeSession()
                closeCamera()
            } catch (_: Exception) {}
            bgThread.quitSafely()
        }
    }

    // ---------------- 相機 ----------------

    fun openCamera() {
        if (device != null || st == null) return
        val id = selectedLensId
        if (id == null) { cb.log("找不到鏡頭"); return }
        try {
            val cc = cameras.getCameraCharacteristics(id)
            sensorOrient = cc.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90
            probeSensorCaps(cc)
            updateRotation(true)
            cb.onPreviewConfig(previewRotationDegrees, previewMirror)
            cb.log("鏡頭 id$id：sensorOrientation=$sensorOrient → 預覽旋轉 ${previewRotationDegrees}°")
            cameras.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(d: CameraDevice) { device = d; createSession() }
                override fun onDisconnected(d: CameraDevice) { d.close(); device = null; setHasVideo(false) }
                override fun onError(d: CameraDevice, e: Int) { d.close(); device = null; setHasVideo(false); cb.log("相機錯誤 $e") }
            }, bg)
        } catch (e: CameraAccessException) { cb.log("開相機失敗：${e.message}") }
        catch (e: SecurityException) { cb.log("開相機失敗：${e.message}") }
    }

    private fun closeCamera() {
        try { device?.close() } catch (_: Exception) {}
        device = null
    }

    fun restartCamera() {
        bgSafe {
            closeSession()
            closeCamera()
            openCamera()
        }
    }

    /** 快速切下一顆鏡頭（供 UI 循環用）。 */
    fun switchCamera() {
        if (_lenses.isEmpty()) return
        val i = _lenses.indexOfFirst { it.id == selectedLensId }
        selectLens(_lenses[(i + 1) % _lenses.size].id)
    }

    private fun setHasVideo(v: Boolean) {
        hasVideo = v
        cb.hasVideoChanged(v)
    }

    private fun probeSensorCaps(cc: CameraCharacteristics) {
        overSizes = null; fpsRanges = null
        try {
            val map = cc.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            if (map != null) {
                val sizes = map.getOutputSizes(ImageFormat.YUV_420_888) ?: map.getOutputSizes(ImageFormat.JPEG)
                if (sizes != null) {
                    val cands = ArrayList<IntArray>()
                    for (s in sizes) {
                        val ar = s.width.toDouble() / s.height
                        if (Math.abs(ar - 4.0 / 3.0) > 0.05) continue // 只要 4:3 原生（CMOS 滿幅）
                        if (s.width > 4096) continue
                        cands.add(intArrayOf(s.width, s.height))
                    }
                    overSizes = cands
                }
            }
            fpsRanges = cc.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
        } catch (_: Exception) {}
    }

    private fun closeOverReader() {
        try { overReader?.close() } catch (_: Exception) {}
        overReader = null; overSurface = null
    }

    /** 超採樣：逼 HAL 全片幅讀出（幀直接丟，只取讀出模式），提升縮小後的銳利度。 */
    private fun setupOverReader() {
        closeOverReader()
        try {
            overW = 0; overH = 0
            overSizes?.let { list ->
                val need = vidW.toLong() * vidH * 2
                var bestPx = Int.MAX_VALUE; var bw = 0; var bh = 0; var bpx = 0L
                for (s in list) {
                    val px = s[0].toLong() * s[1]
                    if (s[0] <= vidW && s[1] <= vidH) continue
                    if (px > bpx) { bpx = px; bw = s[0]; bh = s[1] }
                    if (px < need || px >= bestPx) continue
                    bestPx = px.toInt(); overW = s[0]; overH = s[1]
                }
                if (overW <= 0 && bpx > 0) { overW = bw; overH = bh }
            }
            if (overW <= 0 || overH <= 0) return
            overReader = ImageReader.newInstance(overW, overH, ImageFormat.YUV_420_888, 2).also {
                it.setOnImageAvailableListener({ r ->
                    val img = try { r.acquireLatestImage() } catch (_: Exception) { null }
                    img?.close()
                }, bg)
            }
            overSurface = overReader!!.surface
        } catch (_: Exception) { closeOverReader() }
    }

    private fun pickFpsRange(fps: Int): Range<Int>? {
        try {
            fpsRanges?.let { list ->
                list.firstOrNull { it.lower == fps && it.upper == fps }?.let { return it }
                var best: Range<Int>? = null
                for (r in list) if (r.lower <= fps && fps <= r.upper)
                    if (best == null || (r.upper - r.lower) < (best!!.upper - best!!.lower)) best = r
                best?.let { return it }
            }
        } catch (_: Exception) {}
        return null
    }

    private fun createSession() {
        val d = device ?: return
        val s = st ?: return
        try {
            s.setDefaultBufferSize(vidW, vidH)
            bufferW = vidW; bufferH = vidH
            previewSurface?.release()
            previewSurface = Surface(s)
            val targets = ArrayList<Surface>()
            targets.add(previewSurface!!)
            closeOverReader() // 超採樣停用（穩定優先，避免換檔競態）
            encoderSurface?.let { targets.add(it) }
            overSurface?.let { targets.add(it) }
            val req = d.createCaptureRequest(CameraDevice.TEMPLATE_RECORD)
            targets.forEach { req.addTarget(it) }
            pickFpsRange(vidFps)?.let { req.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) }
            reqBuilder = req
            if (Build.VERSION.SDK_INT >= 30) { try { req.set(CaptureRequest.CONTROL_ZOOM_RATIO, currentZoom) } catch (_: Exception) {} }
            d.createCaptureSession(targets, object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(cs: CameraCaptureSession) {
                    session = cs; sessFails = 0
                    try {
                        cs.setRepeatingRequest(req.build(), null, bg)
                        setHasVideo(true)
                    } catch (e: CameraAccessException) { onSessionFailed("repeating 失敗：${e.message}") }
                }

                override fun onConfigureFailed(cs: CameraCaptureSession) {
                    try { cs.close() } catch (_: Exception) {}
                    session = null
                    onSessionFailed("HAL 拒收此組合")
                }
            }, bg)
        } catch (e: Exception) { onSessionFailed("createSession 例外：${e.message}") }
    }

    private fun closeSession() {
        try { session?.close() } catch (_: Exception) {}
        session = null
        setHasVideo(false)
        closeOverReader()
    }

    /** session 開不起來：先關超採樣重試，再逐檔降（4K→2K→1080P→720P→30fps）。 */
    private fun onSessionFailed(why: String) {
        setHasVideo(false)
        if (!streaming) { cb.log("session 失敗（已停播，不重試）：$why"); return }
        if (sessFails >= 4) { cb.log("session 連敗多次，先停住：$why"); return }
        sessFails++
        if (!overOff && overW > 0) {
            overOff = true
            cb.log("session 失敗，先關超採樣重試…")
            bg.post { createSession() }
            return
        }
        if (QualityTiers.resIdx > 0) {
            QualityTiers.bumpDown()
            overOff = false
            vidMbps = QualityTiers.mbps()
            abrBase = vidMbps; abrStep = 0
            cb.log("此解析度開不起來，自動降檔→${QualityTiers.label()}…")
            applyVideo(QualityTiers.w(), QualityTiers.h(), vidFps, vidMbps, codecName())
            return
        }
        if (QualityTiers.fps() > 30) {
            QualityTiers.fpsIdx = 0; overOff = false
            cb.log("降 FPS→30 重試…")
            applyVideo(vidW, vidH, 30, vidMbps, codecName())
            return
        }
        cb.log("最低檔也開不起來，請重開 App 或換手機：（$why）")
    }

    // ---------------- 編碼 ----------------

    fun startStream() {
        bgSafe {
            try {
                startStreamInternal()
                cb.log("編碼 ${vidW}x${vidH}@${vidFps} ${vidMbps}Mbps")
                cb.videoParams(VideoParams(vidW, vidH, vidFps, codecName(), curRot, curFlip))
                if (sps != null && pps != null) cb.csd(seqHead(), pps!!, !isAvc())
            } catch (e: Exception) {
                cb.log("編碼啟動失敗：${e.message}")
                cb.rejected("encoder", "720p30 h264")
            }
        }
    }

    private fun createEncoder() {
        val fmt = MediaFormat.createVideoFormat(vidCodec, vidW, vidH)
        fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
        fmt.setInteger(MediaFormat.KEY_BIT_RATE, (vidMbps * 1_000_000).toInt())
        fmt.setInteger(MediaFormat.KEY_FRAME_RATE, vidFps)
        fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, if (vidFps >= 60 || vidW >= 2560) 1 else 2)
        try { fmt.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR) } catch (_: Exception) {}
        if (Build.VERSION.SDK_INT >= 30) try { fmt.setInteger("low-latency", 1) } catch (_: Exception) {}
        val enc = MediaCodec.createEncoderByType(vidCodec)
        enc.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        encoderSurface = enc.createInputSurface()
        enc.start()
        encoder = enc
        suckCsd()
        createSession()
    }

    fun stopStream() {
        bgSafe {
            streaming = false
            stopEncoder()
            createSession()
        }
    }

    @Synchronized
    private fun stopEncoder() {
        setHasVideo(false)
        drainThread?.let { try { it.join(500) } catch (_: Exception) {} }
        drainThread = null
        encoder?.let { try { it.stop() } catch (_: Exception) {}; try { it.release() } catch (_: Exception) {} }
        encoder = null
        encoderSurface?.let { try { it.release() } catch (_: Exception) {} }
        encoderSurface = null
        sps = null; pps = null; vps = null; spsLogged = false
    }

    private fun suckCsd() {
        try {
            val of = encoder?.outputFormat ?: return
            for (k in arrayOf("csd-0", "csd-1", "csd-2")) {
                if (!of.containsKey(k)) continue
                val bb = of.getByteBuffer(k) ?: continue
                if (bb.remaining() < 2) continue
                val b = ByteArray(bb.remaining()); bb.get(b)
                parseCsd(b)
            }
        } catch (_: Exception) {}
    }

    private fun drainLoop(myGen: Int) {
        val info = MediaCodec.BufferInfo()
        while (streaming && encoder != null && encGen == myGen) {
            try {
                val enc = encoder ?: break
                val idx = enc.dequeueOutputBuffer(info, 2000)
                if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) { suckCsd(); continue }
                if (idx < 0) continue
                val buf = enc.getOutputBuffer(idx) ?: continue
                val data = ByteArray(info.size)
                buf.get(data)
                val key = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0
                val cfg = (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0
                if (cfg) parseCsd(data)
                if (!cfg || key) {
                    var avcc = data
                    if (key && sps != null && pps != null) avcc = Cam1.concat(seqHead(), data)
                    cb.frame(avcc, key, !isAvc())
                }
                val now = SystemClock.uptimeMillis()
                if (now - lastOrientMs >= 2000) { lastOrientMs = now; cb.orientationChanged(curRot, curFlip) }
                enc.releaseOutputBuffer(idx, false)
            } catch (e: Exception) { cb.log("drain 死：$e"); break }
        }
        if (encGen == myGen) scheduleEncoderRestart(myGen)
    }

    private fun scheduleEncoderRestart(gen: Int) {
        if (!streaming) return
        encRestartTries++
        val waitMs = minOf(30000, 1000 * encRestartTries)
        bg.postDelayed({
            if (!streaming || encGen != gen) return@postDelayed
            try {
                startStreamInternal()
                encRestartTries = 0
            } catch (_: Exception) { scheduleEncoderRestart(gen) }
        }, waitMs.toLong())
    }

    @Synchronized
    private fun startStreamInternal() {
        stopEncoder()
        createEncoder()
        streaming = true
        encGen++
        val myGen = encGen
        drainThread = Thread { drainLoop(myGen) }.also { it.start() }
    }

    // ---------------- CSD 解析 ----------------

    private fun isAvc() = vidCodec.endsWith("avc")

    private fun classifyNal(u: ByteArray) {
        if (u.size < 2) return
        if (isAvc()) {
            when (u[0].toInt() and 0x1F) {
                7 -> if (sps == null) sps = u
                8 -> if (pps == null) pps = u
            }
        } else {
            when (((u[0].toInt() and 0xFF) shr 1) and 0x3F) {
                32 -> if (vps == null) vps = u
                33 -> if (sps == null) sps = u
                34 -> if (pps == null) pps = u
            }
        }
    }

    private fun parseCsd(csd: ByteArray) {
        try {
            if (csd.size < 2) return
            if (Cam1.startsWith(csd, 0, 0, 0, 1) || Cam1.startsWith(csd, 0, 0, 1))
                Cam1.splitAnnexB(csd).forEach { classifyNal(it) }
            else if ((csd[0].toInt() and 0xFF) == 1 && csd.size > 8) parseBoxed(csd)
            else classifyNal(csd)
            if (sps != null && pps != null) cb.csd(seqHead(), pps!!, !isAvc())
        } catch (e: Exception) { cb.log("csd 解析失敗：${e.message}") }
    }

    private fun parseBoxed(csd: ByteArray) {
        if (isAvc()) {
            val nSps = csd[5].toInt() and 0x1F
            var p = 6
            for (k in 0 until nSps) {
                if (p + 2 > csd.size) return
                val len = ((csd[p].toInt() and 0xFF) shl 8) or (csd[p + 1].toInt() and 0xFF)
                if (len <= 0 || p + 2 + len > csd.size) break
                if (sps == null) sps = csd.copyOfRange(p + 2, p + 2 + len)
                p += 2 + len
            }
            if (p >= csd.size) return
            val nPps = csd[p++].toInt() and 0xFF
            for (k in 0 until nPps) {
                if (p + 2 > csd.size) return
                val len = ((csd[p].toInt() and 0xFF) shl 8) or (csd[p + 1].toInt() and 0xFF)
                if (len <= 0 || p + 2 + len > csd.size) break
                if (pps == null) pps = csd.copyOfRange(p + 2, p + 2 + len)
                p += 2 + len
            }
        } else {
            if (csd.size < 23) return
            var p = 22
            val arrays = csd[21].toInt() and 0xFF
            for (a in 0 until arrays) {
                if (p + 3 >= csd.size) break
                val naluType = csd[p].toInt() and 0x3F
                p += 3
                if (p >= csd.size) break
                val cnt = ((csd[p - 1].toInt() and 0xFF) shl 8) or (csd[p].toInt() and 0xFF)
                p++
                for (k in 0 until cnt) {
                    if (p + 2 >= csd.size) break
                    val len = ((csd[p].toInt() and 0xFF) shl 8) or (csd[p + 1].toInt() and 0xFF)
                    if (len <= 0 || p + 2 + len > csd.size) break
                    val u = csd.copyOfRange(p + 2, p + 2 + len)
                    when (naluType) {
                        32 -> if (vps == null) vps = u
                        33 -> if (sps == null) sps = u
                        34 -> if (pps == null) pps = u
                    }
                    p += 2 + len
                }
            }
        }
    }

    private fun seqHead(): ByteArray {
        val s = sps ?: ByteArray(0)
        val p = pps ?: ByteArray(0)
        return if (vps != null && !isAvc()) Cam1.concat(Cam1.annex(vps!!), Cam1.annex(s), Cam1.annex(p))
        else Cam1.concat(Cam1.annex(s), Cam1.annex(p))
    }

    // ---------------- 方向 ----------------

    private fun debounceAngle(a: Int) {
        val now = SystemClock.uptimeMillis()
        if (a != rotCandidate) { rotCandidate = a; rotCandidateSince = now; return }
        if (a != lastAngle && now - rotCandidateSince >= 1000) { lastAngle = a; updateRotation(false) }
    }

    /** 官方定義 OEL 90＝左側在上、270＝右側在上；後鏡 (sensor+角)、前鏡 (sensor-角)＋鏡像。 */
    private fun updateRotation(force: Boolean) {
        if (lastAngle < 0) lastAngle = 0
        val r: Int; val f: Int
        if (useFront) { r = (sensorOrient - lastAngle + 360) % 360; f = 1 }
        else { r = (sensorOrient + lastAngle) % 360; f = 0 }
        if (!force && r == curRot && f == curFlip) return
        curRot = r; curFlip = f
        cb.videoParams(VideoParams(vidW, vidH, vidFps, codecName(), curRot, curFlip))
        cb.orientationChanged(curRot, curFlip)
        cb.onPreviewConfig(previewRotationDegrees, previewMirror)
    }

    // ---------------- 檔位 / 碼率 ----------------

    private fun codecName() = if (isAvc()) "h264" else "hevc"

    fun applyQuality(w: Int, h: Int, fps: Int, mbps: Double, codec: String) {
        bgSafe {
            sessFails = 0
            applyVideo(w, h, fps, mbps, codec)
        }
    }

    @Synchronized
    private fun applyVideo(w: Int, h: Int, fps: Int, mbps: Double, codec: String) {
        val mime = if (codec.equals("hevc", true)) "video/hevc" else "video/avc"
        val old = intArrayOf(vidW, vidH, vidFps)
        val oldCodec = vidCodec
        val oldBr = vidMbps
        val wasStreaming = streaming
        // 完整關閉舊編碼／session 再套新參數，避免半途換檔造成競態閃退
        streaming = false
        stopEncoder()
        closeSession()
        vidCodec = mime; vidW = w; vidH = h; vidFps = fps; vidMbps = mbps
        abrBase = mbps; abrStep = 0; abrGoodStreak = 0
        if (!wasStreaming) {
            // 未開播：重配本機預覽尺寸，讓畫面比例即時正確
            bg.post { if (!streaming) createSession() }
            cb.videoParams(VideoParams(vidW, vidH, vidFps, codecName(), curRot, curFlip))
            return
        }
        try { Thread.sleep(80) } catch (_: InterruptedException) {} // 給 HAL／編碼器收尾時間
        var ok = true
        try { startStreamInternal() } catch (_: Exception) { ok = false }
        if (!ok || !streaming) {
            vidCodec = oldCodec; vidW = old[0]; vidH = old[1]; vidFps = old[2]; vidMbps = oldBr
            try { startStreamInternal() } catch (_: Exception) {}
            cb.rejected("unsupported", "720p30 h264")
            return
        }
        cb.videoParams(VideoParams(vidW, vidH, vidFps, codecName(), curRot, curFlip))
    }

    fun applyBitrateStep() {
        try {
            val br = abrBase * ABR_MUL[abrStep.coerceIn(0, 3)]
            encoder?.let {
                val b = Bundle(); b.putInt(MediaFormat.KEY_BIT_RATE, (br * 1_000_000).toInt())
                it.setParameters(b)
            }
            vidMbps = br
            cb.log("ABR 碼率→$br Mbps（階 $abrStep）")
        } catch (_: Exception) {}
    }

    fun setBitrate(mbps: Double) {
        bgSafe {
            vidMbps = mbps          // 修正：先前沒更新實際碼率，重建仍用舊值
            abrBase = mbps; abrStep = 0
            if (streaming) startStreamInternal()
        }
    }

    fun requestIdr() {
        bgSafe {
            try {
                encoder?.let {
                    val b = Bundle(); b.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0)
                    it.setParameters(b)
                }
            } catch (_: Exception) {}
        }
    }

    /** PC 回授網路狀態→走碼率階梯（每秒一次）。 */
    fun onNetState(loss: Double, jitterMs: Int) {
        bgSafe {
            if (!streaming) return@bgSafe
            val bad = loss > 0.10 || jitterMs > 150
            val ok = loss < 0.02 && jitterMs < 40
            if (bad) {
                abrGoodStreak = 0
                if (abrStep < 3) { abrStep++; applyBitrateStep() }
                if (loss > 0.20) {
                    try {
                        encoder?.let {
                            val b = Bundle(); b.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0)
                            it.setParameters(b)
                        }
                    } catch (_: Exception) {}
                }
            } else if (ok) {
                if (++abrGoodStreak >= 5 && abrStep > 0) { abrGoodStreak = 0; abrStep--; applyBitrateStep() }
            } else abrGoodStreak = 0
        }
    }

    val seqHeadBytes: ByteArray? get() = if (sps != null && pps != null) seqHead() else null
    val ppsBytes: ByteArray? get() = pps
    val isHevc: Boolean get() = !isAvc()

    private val ABR_MUL = doubleArrayOf(1.0, 0.75, 0.5, 0.35)
}

/** 檔位表（4:3 原生，CMOS 滿幅不裁切）。 */
object QualityTiers {
    val resolutions = arrayOf(
        intArrayOf(960, 720), intArrayOf(1440, 1080), intArrayOf(1920, 1440), intArrayOf(2880, 2160)
    )
    val names = arrayOf("720P", "1080P", "2K", "4K")
    val bitrates = doubleArrayOf(4.0, 8.0, 16.0, 32.0)
    val fpsOptions = intArrayOf(30, 60)

    var resIdx = 0
    var fpsIdx = 0

    fun w() = resolutions[resIdx][0]
    fun h() = resolutions[resIdx][1]
    fun fps() = fpsOptions[fpsIdx]
    fun mbps() = bitrates[resIdx]
    fun label() = names[resIdx]
    fun codecFor(): String = if (resIdx >= 2) "hevc" else "h264"

    fun bumpDown() { if (resIdx > 0) resIdx-- }

    fun snapTo(w: Int, h: Int, fps: Int) {
        resolutions.forEachIndexed { i, r -> if (r[0] == w && r[1] == h) resIdx = i }
        fpsOptions.forEachIndexed { i, f -> if (f == fps) fpsIdx = i }
    }
}
