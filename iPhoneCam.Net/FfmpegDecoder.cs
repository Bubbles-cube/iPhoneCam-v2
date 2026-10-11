using System.Collections.Concurrent;
using System.Diagnostics;

namespace iPhoneCam.Net;

/// <summary>
/// 預覽／餵幀解碼器：起一個 ffmpeg 子程序，stdin 餵 Annex-B（H.264/HEVC），
/// stdout 讀回 NV12 原始訊框（全尺寸直通給虛擬攝像頭；App 端再轉半解析小圖做預覽）。
/// 方向轉正用 -vf（transpose／hflip），影像調整用 eq／unsharp，皆在 PC 端後製。
/// 三槽環形緩衝避免每幀配置 LOH；有界餵幀佇列（4 幀）讓網路執行緒永不阻塞。
/// </summary>
public sealed class FfmpegDecoder : IDisposable
{
    private Process? _proc;
    private Thread? _reader, _writer;
    private CancellationTokenSource? _cts;
    private BlockingCollection<byte[]>? _feedQ;
    private long _wroteBytes, _wroteN;
    private readonly object _frameLock = new();
    private byte[][] _ring = [];
    private int _prodSlot;
    private byte[]? _latest;
    private int _latestSlot = -1, _heldSlot = -1;
    private bool _disposed;
    private long _startMs;

    /// <summary>餵幀佇列滿而丟棄的訊框數（解碼器跟不上／pipe 卡住的證據）。</summary>
    public long FeedDrops { get; private set; }
    public string LastFeedLog { get; private set; } = "";
    public bool Running => _proc is { HasExited: false };
    public string LastError { get; private set; } = "";
    public int ShownCount { get; private set; }
    /// <summary>解碼器吐出的總幀數（讀滿一幀即加，不論是否顯示）。</summary>
    public long FramesRead { get; private set; }
    public TimeSpan CpuTime { get { try { return _proc?.TotalProcessorTime ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } } }
    /// <summary>ffmpeg 死亡原因（exit code＋stderr 尾行）。</summary>
    public string DeathReason { get; private set; } = "";
    public List<string> ErrTail { get; } = [];
    public string LastErrLine { get { lock (ErrTail) return ErrTail.Count > 0 ? ErrTail[^1] : ""; } }
    public string LastErrLines(int n)
    {
        lock (ErrTail)
        {
            if (ErrTail.Count == 0) return "";
            int from = Math.Max(0, ErrTail.Count - n);
            return string.Join(" / ", ErrTail.GetRange(from, ErrTail.Count - from));
        }
    }
    public string PipeState()
    {
        try
        {
            return $"reader={(_reader?.IsAlive == true ? "活" : "死")} writer={(_writer?.IsAlive == true ? "活" : "死")}" +
                   $" q={_feedQ?.Count ?? -1} lastErr=[{LastError}]";
        }
        catch { return "?"; }
    }
    public string HwMode { get; private set; } = "";
    public long UpMs { get { try { return _proc != null ? Environment.TickCount64 - _startMs : 0; } catch { return 0; } } }
    /// <summary>輸出（轉正後）尺寸，App 照此開 WriteableBitmap。</summary>
    public int OutW { get; private set; }
    public int OutH { get; private set; }

    /// <summary>手機 rot→ffmpeg 濾鏡。先旋轉轉正、最後再鏡像：前鏡 hflip 若放 transpose 前會在 sensor 空間鏡像＝差 180°。</summary>
    public static string BuildFilter(int rot, bool flip)
    {
        string t = rot switch
        {
            90 => "transpose=1",
            180 => "transpose=1,transpose=1",
            270 => "transpose=2",
            _ => "",
        };
        if (!flip) return t;
        return t.Length > 0 ? t + ",hflip" : "hflip";
    }

    public static string? FindFfmpeg()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(@"C:\Temp\ffmpeg", "ffmpeg-*"))
            {
                string p = Path.Combine(dir, "bin", "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 啟動解碼：rot＝顯示旋轉（順時針 0/90/180/270）、flip＝前鏡鏡像，用 -vf 轉正，
    /// 輸出尺寸＝轉正後尺寸（90/270 寬高互換）。eqB/C/S/U＝亮度-1~1／對比0~2／飽和0~2／銳利-1.5~1.5。
    /// </summary>
    public bool Start(int width, int height, int fps, string ffmpegPath, int rot = 0, bool flip = false,
        string codec = "h264", bool hw = true, double eqB = 0, double eqC = 1, double eqS = 1, double eqU = 0)
    {
        Stop();
        if (width <= 0 || height <= 0) return false;
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string eq = "";
            if (Math.Abs(eqB) > 0.005 || Math.Abs(eqC - 1) > 0.005 || Math.Abs(eqS - 1) > 0.005)
                eq += string.Format(ci, ",eq=brightness={0:F2}:contrast={1:F2}:saturation={2:F2}", eqB, eqC, eqS);
            if (Math.Abs(eqU) > 0.05) eq += string.Format(ci, ",unsharp=5:5:{0:F2}", eqU);
            string vf = BuildFilter(rot, flip);
            // NV12 直通：硬解 hwdownload 回系統記憶體即 NV12，不轉 bgra（轉檔是 CPU 大戶，留給預覽小圖）。
            string vffull = (hw ? "hwdownload,format=nv12" : "format=nv12") + eq + (vf.Length > 0 ? "," + vf : "");
            HwMode = hw ? "硬解" : "軟解";
            _startMs = Environment.TickCount64;
            if (rot % 180 == 0) { OutW = width; OutH = height; }
            else { OutW = height; OutH = width; }
            DeathReason = "";
            lock (ErrTail) ErrTail.Clear();
            string demuxer = codec == "hevc" ? "hevc" : "h264";
            // 中途加入首口多半是無 SPS 的 P 幀：probe 給 1 秒耐心等 IDR。
            // 注意不可加 -fflags nobuffer（實測會讓 h264 解 2 幀就停）。
            var psi = new ProcessStartInfo(ffmpegPath,
                $"-hide_banner -loglevel error -probesize 500000 -analyzeduration 1000000 " +
                (hw ? "-hwaccel d3d11va -hwaccel_output_format d3d11 " : "") +
                $"-f {demuxer} -framerate {fps} -i pipe:0 -vf {vffull} -f rawvideo -pix_fmt nv12 pipe:1")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi);
            if (_proc == null) { LastError = "ffmpeg 起不來"; return false; }
            var proc = _proc;
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (ErrTail) { ErrTail.Add(e.Data); while (ErrTail.Count > 30) ErrTail.RemoveAt(0); }
            };
            proc.BeginErrorReadLine();
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                string tail;
                lock (ErrTail) tail = ErrTail.Count > 0 ? ErrTail[^1] : "(無 stderr)";
                try { DeathReason = $"exit {proc.ExitCode}：{tail}"; } catch { }
            };
            _cts = new CancellationTokenSource();
            FeedDrops = 0;
            _feedQ = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 4);
            _writer = new Thread(() => WriteLoop(_cts.Token)) { IsBackground = true };
            _writer.Start();
            try
            {
                int size = OutW * OutH * 3 / 2; // NV12
                _ring = [new byte[size], new byte[size], new byte[size]];
                _prodSlot = 0; _latest = null; _latestSlot = -1; _heldSlot = -1;
            }
            catch { _ring = []; }
            _reader = new Thread(() => ReadLoop(_cts.Token)) { IsBackground = true };
            _reader.Start();
            return true;
        }
        catch (Exception e) { LastError = "start: " + e.Message; return false; }
    }

    public void Feed(byte[] annexb)
    {
        try
        {
            var q = _feedQ;
            if (q == null || q.IsAddingCompleted) return;
            if (!q.TryAdd(annexb)) FeedDrops++; // 滿了丟掉（直播跟不上就該丟，不能擋網路執行緒）
        }
        catch { }
    }

    private void WriteLoop(CancellationToken ct)
    {
        try
        {
            var stdin = _proc?.StandardInput.BaseStream;
            var q = _feedQ;
            if (stdin == null || q == null) return;
            while (!ct.IsCancellationRequested)
            {
                byte[] b;
                try { b = q.Take(ct); }
                catch (OperationCanceledException) { return; }
                catch (InvalidOperationException) { return; }
                try { stdin.Write(b, 0, b.Length); _wroteBytes += b.Length; _wroteN++; }
                catch (Exception e) { LastError = "feed: " + e.Message; return; }
                if (_wroteN == 1 || _wroteN % 150 == 0) LastFeedLog = $"wrote {_wroteN}包 {_wroteBytes}B";
            }
        }
        catch (Exception e) { LastError = "feedloop: " + e.Message; }
    }

    /// <summary>取最新一幀（顯示後該槽不再被讀端覆寫）。</summary>
    public byte[]? TakeLatest()
    {
        lock (_frameLock)
        {
            var f = _latest;
            _heldSlot = _latestSlot;
            _latest = null;
            if (f != null) ShownCount++;
            return f;
        }
    }

    private void ReadLoop(CancellationToken ct)
    {
        try
        {
            var stdout = _proc!.StandardOutput.BaseStream;
            int size = OutW * OutH * 3 / 2; // NV12（須與 Start 的 ring 一致，不一致讀端會靜默死）
            while (!ct.IsCancellationRequested)
            {
                int s, held;
                lock (_frameLock)
                {
                    held = _heldSlot; s = _prodSlot % 3;
                    if (s == held) s = (s + 1) % 3;
                    if (s == held) s = (s + 1) % 3;
                }
                if (_ring.Length != 3 || _ring[s].Length != size) return; // 尺寸變了（旋轉重啟），等新實例
                byte[] buf = _ring[s];
                int got = 0;
                while (got < size)
                {
                    int n = stdout.Read(buf, got, size - got);
                    if (n == 0) return; // ffmpeg 死了
                    got += n;
                }
                FramesRead++;
                lock (_frameLock) { _latest = buf; _latestSlot = s; _prodSlot = s + 1; }
            }
        }
        catch (Exception e) { LastError = "read: " + e.Message; }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _feedQ?.CompleteAdding(); } catch { }
        try
        {
            if (_proc is { HasExited: false })
            {
                try { _proc.StandardInput.Close(); } catch { }
                if (!_proc.WaitForExit(500)) try { _proc.Kill(); } catch { }
            }
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        _ring = [];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
