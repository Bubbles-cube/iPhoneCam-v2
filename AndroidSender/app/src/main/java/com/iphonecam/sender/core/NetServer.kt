package com.iphonecam.sender.core

import android.os.Build
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.util.Collections

/**
 * 網路層（SPEC-v4 §11）：手機＝TCP 伺服器（19300）＋UDP 信標（19301）。
 * 控制走 TCP（PC 主動連入）；影像可走 UDP（CUM1，19302，分片＋NACK 重送），斷流自動回退 TCP。
 */
class NetServer(private val cb: Callbacks) {

    interface Callbacks {
        fun log(msg: String)
        fun onSetVideo(width: Int, height: Int, fps: Int, mbps: Double, codec: String)
        fun onSetImage(json: JSONObject)
        fun onIdrRequest()
        fun onNetState(loss: Double, jitterMs: Int)
        fun onConnChanged(connected: Boolean, ip: String)
    }

    companion object { private const val TAG = "IPC-Net" }

    @Volatile private var running = false
    private var serverThread: Thread? = null
    private var beaconThread: Thread? = null
    private var serverSocket: ServerSocket? = null

    private var clientSocket: Socket? = null
    @Volatile private var clientOut: OutputStream? = null
    @Volatile private var clientIp = "-"
    @Volatile private var lastPcIp = ""

    // UDP 影像
    private var udpSock: DatagramSocket? = null
    private var udpAddr: InetAddress? = null
    private var udpPort = Cam1.UDP_VIDEO_PORT
    @Volatile private var useUdp = false
    private var udpSeq = 0
    private val udpCache = object : LinkedHashMap<Int, ByteArray>(40, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<Int, ByteArray>?) = size > 32
    }

    @Volatile var beaconMax: String = "720P30"
    @Volatile var beaconHevc: Boolean = false

    // ---------------- 生命週期 ----------------

    fun start() {
        if (running) return
        running = true
        startServer()
        startBeacon()
    }

    fun stop() {
        running = false
        closeClient()
        useUdp = false
        try { udpSock?.close() } catch (_: Exception) {}
        udpSock = null
        try { serverSocket?.close() } catch (_: Exception) {}
        serverSocket = null
        try { serverThread?.join(500) } catch (_: Exception) {}
        serverThread = null
        try { beaconThread?.join(500) } catch (_: Exception) {}
        beaconThread = null
    }

    // ---------------- 傳送 ----------------

    fun sendFrame(avcc: ByteArray, key: Boolean, hevc: Boolean) {
        if (avcc.isEmpty()) return
        if (useUdp) sendUdp(avcc, key, hevc) else sendTcpVideo(avcc)
    }

    private fun sendTcpVideo(avcc: ByteArray) {
        val o = clientOut ?: return
        try { synchronized(this) { Cam1.writeFrame(o, Cam1.T_VIDEO, avcc) } }
        catch (e: IOException) { closeClient() }
    }

    fun sendControl(json: String) {
        val o = clientOut ?: return
        try {
            synchronized(this) {
                Cam1.writeFrame(o, Cam1.T_CONTROL, json.toByteArray(Charsets.UTF_8))
            }
        } catch (e: IOException) { closeClient() }
    }

    fun sendControl(kind: String, body: JSONObject?) {
        val o = JSONObject(body?.toString() ?: "{}")
        o.put("kind", kind)
        sendControl(o.toString())
    }

    // ---------------- TCP 伺服器 ----------------

    private fun startServer() {
        serverThread = Thread {
            try {
                ServerSocket(Cam1.CAM_PORT).use { srv ->
                    serverSocket = srv
                    while (running) {
                        val c = srv.accept()
                        try { c.tcpNoDelay = true } catch (_: Exception) {}
                        closeClient()
                        clientSocket = c
                        clientIp = c.inetAddress.hostAddress ?: "-"
                        lastPcIp = clientIp
                        clientOut = c.getOutputStream()
                        cb.log("PC 連入：$clientIp")
                        cb.onConnChanged(true, clientIp)
                        sendControl("hello", JSONObject().apply {
                            put("app", "iPhoneCam-Android")
                            put("version", "v4")
                            put("device", Build.MODEL)
                        })
                        readLoop(c.getInputStream())
                        val gone = clientIp
                        closeClient()
                        cb.log("PC 斷線：$gone")
                        cb.onConnChanged(false, "")
                    }
                }
            } catch (e: IOException) { if (running) cb.log("監聽失敗：${e.message}") }
            catch (_: Exception) {}
        }.also { it.start() }
    }

    private fun readLoop(inp: InputStream) {
        try {
            while (running && clientSocket?.isClosed == false) {
                val hdr = Cam1.readExact(inp, Cam1.HEADER)
                val len = ((hdr[13].toInt() and 0xFF) shl 24) or ((hdr[14].toInt() and 0xFF) shl 16) or
                        ((hdr[15].toInt() and 0xFF) shl 8) or (hdr[16].toInt() and 0xFF)
                val pl = if (len > 0) Cam1.readExact(inp, len) else ByteArray(0)
                if (hdr[4] == Cam1.T_CONTROL) onControl(String(pl, Charsets.UTF_8))
            }
        } catch (_: Exception) {}
    }

    private fun onControl(json: String) {
        try {
            val o = JSONObject(json)
            when (o.optString("kind")) {
                "set_video" -> {
                    val w = o.optInt("width", 960)
                    val h = o.optInt("height", 720)
                    val fps = o.optInt("fps", 30)
                    val br = o.optDouble("bitrate_mbps", 4.0)
                    val codec = o.optString("codec", "h264")
                    cb.onSetVideo(w, h, fps, br, codec)
                }
                "set_image" -> o.optJSONObject("beauty")?.let { cb.onSetImage(it) }
                "udp_ready" -> {
                    var ip = o.optString("host", "").ifEmpty { lastPcIp }
                    if (ip.isEmpty()) ip = clientIp
                    udpSetup(ip, o.optInt("port", Cam1.UDP_VIDEO_PORT))
                }
                "udp_lost" -> udpDown()
                "nack" -> o.optJSONArray("seqs")?.let {
                    val seqs = IntArray(it.length()) { i -> it.optInt(i) }
                    udpResend(seqs)
                }
                "net_state" -> cb.onNetState(o.optDouble("loss", 0.0), o.optInt("jitterMs", 0))
                "idr_request" -> cb.onIdrRequest()
            }
        } catch (_: Exception) {}
    }

    private fun closeClient() {
        try { clientSocket?.close() } catch (_: Exception) {}
        clientSocket = null
        clientOut = null
        useUdp = false
    }

    // ---------------- UDP 影像 ----------------

    private fun udpSetup(ip: String, port: Int) {
        try {
            if (udpSock == null || udpSock!!.isClosed) udpSock = DatagramSocket()
            udpAddr = InetAddress.getByName(ip)
            udpPort = if (port > 0) port else Cam1.UDP_VIDEO_PORT
            synchronized(udpCache) { udpCache.clear() }
            useUdp = true
            cb.log("UDP 影像啟動：$ip:$udpPort")
        } catch (e: Exception) { cb.log("UDP 啟動失敗（續用 TCP）：${e.message}"); useUdp = false }
    }

    private fun sendUdp(avcc: ByteArray, key: Boolean, hevc: Boolean) {
        val ds = udpSock ?: return
        val addr = udpAddr ?: return
        if (ds.isClosed) return
        try {
            val seq = synchronized(this) { udpSeq++ }
            synchronized(udpCache) { udpCache[seq] = avcc }
            val flags = (if (key) 1 else 0) or (if (hevc) 2 else 0)
            val ts = System.nanoTime() / 1000L
            for (pkt in Cam1.udpPackets(seq, ts, flags, avcc))
                ds.send(DatagramPacket(pkt, pkt.size, addr, udpPort))
        } catch (_: Exception) {}
    }

    private fun udpResend(seqs: IntArray) {
        val ds = udpSock ?: return
        val addr = udpAddr ?: return
        try {
            for (seq in seqs) {
                val avcc = synchronized(udpCache) { udpCache[seq] } ?: continue
                val ts = System.nanoTime() / 1000L
                for (pkt in Cam1.udpPackets(seq, ts, 0, avcc))
                    ds.send(DatagramPacket(pkt, pkt.size, addr, udpPort))
            }
        } catch (_: Exception) {}
    }

    private fun udpDown() {
        useUdp = false
        cb.log("UDP 斷流，切回 TCP（不斷線）")
    }

    // ---------------- 信標 ----------------

    private fun startBeacon() {
        beaconThread = Thread {
            try {
                val ds = DatagramSocket()
                ds.broadcast = true
                while (running) {
                    try {
                        val o = JSONObject()
                        o.put("kind", "beacon")
                        o.put("name", Build.MODEL)
                        o.put("ip", localIp())
                        o.put("port", Cam1.CAM_PORT)
                        val arr = JSONArray().put("h264")
                        if (beaconHevc) arr.put("hevc")
                        o.put("codec", arr)
                        o.put("max", beaconMax)
                        val b = o.toString().toByteArray(Charsets.UTF_8)
                        ds.send(DatagramPacket(b, b.size, InetAddress.getByName("255.255.255.255"), Cam1.BEACON_PORT))
                        // 家用 AP 廣播常掉包：已知 PC 補一發單播
                        val pc = lastPcIp
                        if (pc.length > 7 && !pc.contains(':'))
                            try { ds.send(DatagramPacket(b, b.size, InetAddress.getByName(pc), Cam1.BEACON_PORT)) } catch (_: Exception) {}
                    } catch (_: Exception) {}
                    Thread.sleep(2000)
                }
                ds.close()
            } catch (_: Exception) {}
        }.also { it.start() }
    }

    private fun localIp(): String {
        try {
            for (ni in Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!ni.isUp || ni.isLoopback) continue
                for (a in Collections.list(ni.inetAddresses)) {
                    if (a.isSiteLocalAddress && a.hostAddress?.contains(':') == false) return a.hostAddress!!
                }
            }
        } catch (_: Exception) {}
        return "0.0.0.0"
    }
}
