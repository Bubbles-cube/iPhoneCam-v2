package com.iphonecam.sender.core

import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * CAM1 訊框格式（SPEC-v3 §4，凍結）：
 * [MAGIC "CAM1" 4B][型別 1B][時間戳微秒 8B][長度 4B][酬載]，全部大端序。
 * 手機是伺服器（TCP 19300），PC 主動連入；影像可走 UDP（CUM1，19302）。
 */
object Cam1 {
    const val CAM_PORT = 19300
    const val BEACON_PORT = 19301
    const val UDP_VIDEO_PORT = 19302
    const val UDP_MTU = 1400
    const val HEADER = 17
    const val UDP_HEADER = 21
    const val UDP_MAGIC = 0x43554D31.toInt()
    const val T_VIDEO: Byte = 0x01
    const val T_CONTROL: Byte = 0x03

    private val MAGIC = byteArrayOf(0x43, 0x41, 0x4D, 0x31) // "CAM1"

    fun writeFrame(out: OutputStream, type: Byte, payload: ByteArray) {
        val ts = System.nanoTime() / 1000L
        val h = ByteBuffer.allocate(HEADER).order(ByteOrder.BIG_ENDIAN)
        h.put(MAGIC); h.put(type); h.putLong(ts); h.putInt(payload.size)
        out.write(h.array())
        out.write(payload)
        out.flush()
    }

    fun readExact(inp: InputStream, n: Int): ByteArray {
        val b = ByteArray(n)
        var got = 0
        while (got < n) {
            val r = inp.read(b, got, n - got)
            if (r < 0) throw java.io.IOException("closed")
            got += r
        }
        return b
    }

    /** CUM1：把一個 AVCC 幀切成 UDP 分片（大端序）。 */
    fun udpPackets(seq: Int, tsUs: Long, flags: Int, avcc: ByteArray): List<ByteArray> {
        var total = (avcc.size + UDP_MTU - 1) / UDP_MTU
        if (total < 1) total = 1
        if (total > 600) return emptyList()
        val out = ArrayList<ByteArray>(total)
        for (i in 0 until total) {
            val off = i * UDP_MTU
            val len = minOf(UDP_MTU, avcc.size - off)
            val b = ByteBuffer.allocate(UDP_HEADER + len).order(ByteOrder.BIG_ENDIAN)
            b.putInt(UDP_MAGIC); b.putInt(seq); b.putLong(tsUs); b.put(flags.toByte())
            b.putShort(i.toShort()); b.putShort(total.toShort())
            b.put(avcc, off, len)
            out.add(b.array())
        }
        return out
    }

    // ---- Annex-B / NAL 工具 ----
    fun annex(nal: ByteArray): ByteArray {
        val out = ByteArray(nal.size + 4)
        out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 1
        System.arraycopy(nal, 0, out, 4, nal.size)
        return out
    }

    fun concat(vararg parts: ByteArray): ByteArray {
        var n = 0
        for (p in parts) n += p.size
        val out = ByteArray(n)
        var o = 0
        for (p in parts) { System.arraycopy(p, 0, out, o, p.size); o += p.size }
        return out
    }

    fun startsWith(b: ByteArray, vararg pre: Int): Boolean {
        if (b.size < pre.size) return false
        for (i in pre.indices) if ((b[i].toInt() and 0xFF) != pre[i]) return false
        return true
    }

    fun splitAnnexB(b: ByteArray): List<ByteArray> {
        val out = ArrayList<ByteArray>()
        var start = -1
        var i = 0
        while (i + 3 < b.size) {
            val sc = if (b[i].toInt() == 0 && b[i + 1].toInt() == 0)
                (if (b[i + 2].toInt() == 1) 3 else if (b[i + 2].toInt() == 0 && b[i + 3].toInt() == 1) 4 else 0) else 0
            if (sc > 0) {
                if (start >= 0 && i > start) out.add(b.copyOfRange(start, i))
                start = i + sc
                i += sc - 1
            }
            i++
        }
        if (start in 0 until b.size) out.add(b.copyOfRange(start, b.size))
        return out
    }
}
