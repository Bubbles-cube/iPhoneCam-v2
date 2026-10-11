using System.Net;
using System.Net.Sockets;

namespace iPhoneCam.Net;

/// <summary>
/// CUM1 UDP 即時影像接收（SPEC-v4 §11.1 加分項）：分片重組＋抖動緩衝＋NACK。
/// 格式（全大端）：[MAGIC "CUM1" 4B][seq u32][tsUs u64][flags 1B][fragIdx u16][fragTotal u16][payload≤1400B]；
/// flags bit0＝keyframe、bit1＝hevc。酬載與 TCP 路徑同為 Annex-B，下游毋須分辨來源。
/// 策略：按 seq 排序輸出（完整幀等 60ms 吸抖動）；120ms 缺件發 NACK；150ms 未補即跳過記 lost；500ms 清殘。
/// </summary>
public sealed class UdpVideoReceiver : IDisposable
{
    public const uint Magic = 0x43554D31; // "CUM1"
    public const int HeaderSize = 21;
    public const int DefaultPort = Cam1.UdpVideoPort;
    public const int HoldMs = 60;
    public const int NackMs = 120;
    public const int SkipMs = 150;
    public const int DropMs = 500;

    public sealed record VideoFrame(byte[] Avcc, bool Key, long TsUs, int Seq);

    public event Action<VideoFrame>? FrameReady;
    public event Action<int[]>? NackNeeded;

    private UdpClient? _udp;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private readonly object _lock = new();
    private readonly Dictionary<int, FragBuf> _partial = new();
    private readonly SortedDictionary<int, ReadyFrame> _jitter = new();
    private readonly HashSet<int> _nackPend = new();
    private readonly Dictionary<int, long> _nextOutDropAt = new();
    private int _nextOut = -1;      // 下一個要輸出的 seq（-1＝尚未對齊）
    private long _lastNackMs;
    private long _lastArrivalMs = -1;
    private long _lastArrivalTs;

    public int Port { get; }

    public long Received { get; private set; }
    public long Complete { get; private set; }
    public long Lost { get; private set; }
    public long Duplicates { get; private set; }
    public long WinRecv { get; private set; }
    public long WinLost { get; private set; }
    public double JitterMs { get; private set; }

    public UdpVideoReceiver(int port = DefaultPort) => Port = port;

    private sealed class FragBuf
    {
        public byte[][] Frags = [];
        public int Got;
        public int Total;
        public bool Key;
        public long TsUs;
        public long FirstMs;
    }

    private sealed record ReadyFrame(byte[] Avcc, bool Key, long TsUs, int Seq, long ReadyMs);

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        try { _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
        lock (_lock) { _partial.Clear(); _jitter.Clear(); _nextOut = -1; }
        _thread = new Thread(() => RecvLoop(_cts.Token)) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
    }

    /// <summary>取一秒統計（loss/jitter 回授給手機走 ABR），順手清零視窗計數。</summary>
    public (double loss, int jitterMs) TakeSecondStats()
    {
        lock (_lock)
        {
            long r = WinRecv, l = WinLost;
            WinRecv = 0; WinLost = 0;
            double loss = (r + l) > 0 ? (double)l / (r + l) : 0;
            return (loss, (int)Math.Round(JitterMs));
        }
    }

    private void RecvLoop(CancellationToken ct)
    {
        var udp = _udp;
        if (udp == null) return;
        IPEndPoint remote = new(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            byte[] dg;
            try { dg = udp.Receive(ref remote); }
            catch { return; }
            try { OnDatagram(dg); } catch { }
        }
    }

    private void OnDatagram(byte[] dg)
    {
        if (dg.Length < HeaderSize + 1) return;
        uint magic = (uint)((dg[0] << 24) | (dg[1] << 16) | (dg[2] << 8) | dg[3]);
        if (magic != Magic) return;
        int seq = (dg[4] << 24) | (dg[5] << 16) | (dg[6] << 8) | dg[7];
        long ts = 0;
        for (int i = 0; i < 8; i++) ts = (ts << 8) | dg[8 + i];
        byte flags = dg[16];
        int fragIdx = (dg[17] << 8) | dg[18];
        int fragTotal = (dg[19] << 8) | dg[20];
        if (fragTotal <= 0 || fragTotal > 600 || fragIdx < 0 || fragIdx >= fragTotal) return;
        int payLen = dg.Length - HeaderSize;
        long nowMs = Environment.TickCount64;

        List<int>? nack = null;
        List<VideoFrame>? ready = null;
        lock (_lock)
        {
            Received++; WinRecv++;
            // 到達間隔抖動（RFC3550 式指數平均）
            if (_lastArrivalMs >= 0)
            {
                long arrDiff = nowMs - _lastArrivalMs;
                double tsDiffMs = (ts - _lastArrivalTs) / 1000.0;
                double d = Math.Abs(arrDiff - tsDiffMs);
                JitterMs += (d - JitterMs) / 16.0;
            }
            _lastArrivalMs = nowMs;
            _lastArrivalTs = ts;

            if (!_partial.TryGetValue(seq, out var fb))
            {
                fb = new FragBuf { Frags = new byte[fragTotal][], Total = fragTotal,
                    Key = (flags & 1) != 0, TsUs = ts, FirstMs = nowMs };
                _partial[seq] = fb;
            }
            else if (fb.Total != fragTotal)
            {
                // 同 seq 片數對不上（串流重啟殘留）：丟舊重收
                fb.Frags = new byte[fragTotal][]; fb.Got = 0; fb.Total = fragTotal; fb.FirstMs = nowMs;
            }

            if (fb.Frags[fragIdx] == null)
            {
                var part = new byte[payLen];
                Buffer.BlockCopy(dg, HeaderSize, part, 0, payLen);
                fb.Frags[fragIdx] = part;
                fb.Got++;
            }
            else Duplicates++;

            if (fb.Got >= fb.Total)
            {
                int total = 0;
                foreach (var f in fb.Frags) total += f.Length;
                var avcc = new byte[total];
                int off = 0;
                foreach (var f in fb.Frags) { Buffer.BlockCopy(f, 0, avcc, off, f.Length); off += f.Length; }
                _partial.Remove(seq);
                Complete++;
                if (_nextOut < 0 && fb.Key) _nextOut = seq; // 從 keyframe 對齊，避免半路加入解不出
                _jitter[seq] = new ReadyFrame(avcc, fb.Key, ts, seq, nowMs);
            }

            ready = DrainReady(nowMs);
            nack = HandleGap(nowMs);
            Scavenge(nowMs, ref nack);
        }

        if (ready != null)
            foreach (var f in ready)
                try { FrameReady?.Invoke(f); } catch { }
        if (nack is { Count: > 0 })
            try { NackNeeded?.Invoke(nack.ToArray()); } catch { }
    }

    /// <summary>照 seq 順序輸出：已齊＋放足 HoldMs 才放行。</summary>
    private List<VideoFrame> DrainReady(long nowMs)
    {
        var ready = new List<VideoFrame>();
        while (true)
        {
            if (_nextOut < 0)
            {
                int? best = null;
                foreach (var kv in _jitter) { if (kv.Value.Key) { best = kv.Key; break; } }
                if (best == null)
                {
                    // 還沒有 key：把放超時的舊幀清掉，避免爆
                    var drop = _jitter.Where(kv => nowMs - kv.Value.ReadyMs > DropMs).Select(kv => kv.Key).ToList();
                    foreach (int s in drop) { _jitter.Remove(s); Lost++; WinLost++; }
                    break;
                }
                _nextOut = best.Value;
            }
            if (!_jitter.TryGetValue(_nextOut, out var rf)) break;
            if (nowMs - rf.ReadyMs < HoldMs) break;
            _jitter.Remove(_nextOut);
            ready.Add(new VideoFrame(rf.Avcc, rf.Key, rf.TsUs, rf.Seq));
            _nextOut++;
        }
        return ready;
    }

    /// <summary>缺口處理：缺席且非組裝中→排 NACK；擋超 SkipMs 就跳過記 lost。</summary>
    private List<int>? HandleGap(long nowMs)
    {
        List<int>? nack = null;
        if (_nextOut >= 0 && !_jitter.ContainsKey(_nextOut))
        {
            bool assembling = _partial.TryGetValue(_nextOut, out var pfb) && nowMs - pfb.FirstMs < DropMs;
            if (!assembling)
            {
                _nackPend.Add(_nextOut);
                if (nowMs - _lastNackMs > 200 && _nackPend.Count > 0)
                {
                    _lastNackMs = nowMs;
                    nack = new List<int>(_nackPend);
                    _nackPend.Clear();
                }
                if (!_nextOutDropAt.TryGetValue(_nextOut, out var t0)) _nextOutDropAt[_nextOut] = nowMs;
                else if (nowMs - t0 > SkipMs)
                {
                    _nextOutDropAt.Remove(_nextOut);
                    _nextOut++;
                    Lost++; WinLost++;
                }
            }
        }
        else _nextOutDropAt.Remove(_nextOut);
        return nack;
    }

    /// <summary>清陳舊：未完成組裝、過期抖動殘留、來晚的舊幀。</summary>
    private void Scavenge(long nowMs, ref List<int>? nack)
    {
        if (_partial.Count > 128)
        {
            foreach (int s in _partial.Where(kv => nowMs - kv.Value.FirstMs > DropMs).Select(kv => kv.Key).ToList())
            { _partial.Remove(s); Lost++; WinLost++; }
        }

        foreach (int s in _jitter.Where(kv => kv.Key < _nextOut || nowMs - kv.Value.ReadyMs > DropMs)
                     .Select(kv => kv.Key).ToList())
        { _jitter.Remove(s); Lost++; WinLost++; }

        if (_jitter.Count > 256)
        {
            // 極端擁塞保護：只留最新 64 個
            var keys = new List<int>(_jitter.Keys);
            for (int i = 0; i < keys.Count - 64; i++) { _jitter.Remove(keys[i]); Lost++; WinLost++; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _cts?.Dispose(); } catch { }
    }
}
