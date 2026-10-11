using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using DirectN;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using iPhoneCam.Net;

namespace iPhoneCam.App;

public partial class MainWindow : Window
{
    private const string FriendlyLandscape = "泡泡直播助手 橫向";
    private const string FriendlyPortrait = "泡泡直播助手 直向";
    private const string SourceIdLandscape = "{8136b935-e87f-4c31-9456-31eedfd84776}";
    private const string SourceIdPortrait = "{7e5c1a20-9b34-4d61-8f22-3c6a9b0d1e54}";
    private IComObject<IMFVirtualCamera>? _camL;
    private bool _mfStarted;

    private int _camW = 960, _camH = 720;
    private static bool IsCamTier(int w, int h) =>
        (w == 960 && h == 720) || (w == 1440 && h == 1080) ||
        (w == 1920 && h == 1440) || (w == 2880 && h == 2160) ||
        (w == 720 && h == 960) || (w == 1080 && h == 1440) ||
        (w == 1440 && h == 1920) || (w == 2160 && h == 2880) ||
        (w == 1280 && h == 720) || (w == 1920 && h == 1080) ||
        (w == 2560 && h == 1440) || (w == 3840 && h == 2160);

    private readonly object _lock = new();
    private BeaconListener? _beacon;
    private Cam1Client? _client;
    private UdpVideoReceiver? _udp;
    private bool _useUdp;
    private bool _connected;
    private long _lastUdpMs, _connMs;
    private long _udpWinFrames, _udpWinBytes;
    private DateTime _udpWinStart = DateTime.UtcNow;
    private FileStream? _dump;
    private bool _saving;

    private readonly FfmpegDecoder _decoder = new();
    private readonly SharedFrameWriter _frameWriter = new();
    private CancellationTokenSource? _pumpCts;
    private int _vidW, _vidH, _vidFps = 30, _vidRot;
    private bool _vidFlip;
    private string _vidCodec = "h264", _ffKey = "", _ffBase = "", _previewLabel = "";
    private long _lastFfStartMs, _lastNetLogMs, _lastShownLogMs, _shown, _drops, _netIn;
    private string _firstLoggedFor = "", _dropLoggedFor = "";
    private bool _hwBroken;
    private byte[]? _sps, _pps, _seqHead;

    private WriteableBitmap? _bitmapA, _bitmapB;
    private bool _useA;
    private DateTime _lastShown = DateTime.MinValue;
    private WriteableBitmap? _lastShownBitmap;

    private bool _uiReady;
    private string _phoneName = "", _phoneIp = "";

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ipc-preview.log");
    private static void PLog(string s)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); } catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1; DwmSetWindowAttribute(h, 20, ref dark, sizeof(int)); // 暗色模式
            System.Windows.Interop.HwndSource.FromHwnd(h)?.AddHook(WndProc);
        }
        catch { }
    }

    private const int WM_NCHITTEST = 0x0084;

    /// <summary>透明無框視窗：自製邊緣命中測試，讓使用者可拖拉四邊／四角調整大小。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            const double b = 8;
            int sx = (short)(l.ToInt64() & 0xFFFF);
            int sy = (short)((l.ToInt64() >> 16) & 0xFFFF);
            var p = PointFromScreen(new Point(sx, sy));
            double x = p.X, y = p.Y, ww = ActualWidth, hh = ActualHeight;
            bool left = x < b, right = x > ww - b, top = y < b, bottom = y > hh - b;
            int hit = 0;
            if (top && left) hit = 13; else if (top && right) hit = 14;
            else if (bottom && left) hit = 16; else if (bottom && right) hit = 17;
            else if (left) hit = 10; else if (right) hit = 11;
            else if (top) hit = 12; else if (bottom) hit = 15;
            if (hit != 0) { handled = true; return (IntPtr)hit; }
        }
        return IntPtr.Zero;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TxtStatus.Text = "虛擬相機：啟動中…";
        try
        {
            ChkSoftDec.IsChecked = File.Exists(@"C:\Temp\ipc-softdec");
            ShowTab("Device");
            HighlightNav("Home");
            _uiReady = true;
        }
        catch { }
        var (ok, msg) = await Task.Run(StartCamera);
        TxtStatus.Text = msg;
        StartBeacon();
    }

    // ================= 裝置發現 / 連線 =================

    private void StartBeacon()
    {
        try
        {
            _beacon ??= new BeaconListener();
            _beacon.DevicesChanged += devs => Dispatcher.Invoke(() => RefreshPhones(devs));
            _beacon.Start();
            TxtNet.Text = "搜尋中…（手機開 App 按開始廣播）";
        }
        catch (Exception ex) { TxtNet.Text = "搜尋啟動失敗：" + ex.Message; }
    }

    private void RefreshPhones(IReadOnlyList<PhoneDevice> devs)
    {
        object? sel = LstPhones.SelectedItem;
        LstPhones.Items.Clear();
        foreach (var d in devs) LstPhones.Items.Add(d);
        LstPhones.DisplayMemberPath = "Display";
        if (sel is PhoneDevice s)
            foreach (var item in LstPhones.Items)
                if (item is PhoneDevice d && d.Ip == s.Ip) { LstPhones.SelectedItem = item; break; }
        if (LstPhones.SelectedItem == null && LstPhones.Items.Count > 0) LstPhones.SelectedIndex = 0;
        if (_connected) return;
        TxtNet.Text = $"搜尋中…找到 {devs.Count} 台";
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (LstPhones.SelectedItem is not PhoneDevice d) { TxtNet.Text = "先選一台手機（或手動輸 IP）"; return; }
        await ConnectPhone(d.Ip, d.Port);
    }

    private async void BtnManual_Click(object sender, RoutedEventArgs e)
    {
        string ip = TxtIp.Text.Trim();
        if (string.IsNullOrEmpty(ip)) { TxtNet.Text = "先輸手機 IP"; return; }
        await ConnectPhone(ip, Cam1.Port);
    }

    private async Task ConnectPhone(string ip, int port)
    {
        try { _client?.Dispose(); } catch { }
        try
        {
            if (LstPhones.SelectedItem is PhoneDevice sd) { _phoneName = sd.Name; _phoneIp = sd.Ip; }
            else { _phoneName = "手機"; _phoneIp = ip; }
        }
        catch { _phoneName = "手機"; _phoneIp = ip; }

        _client = new Cam1Client();
        _client.Connected += ep => Dispatcher.Invoke(() =>
        {
            TxtNet.Text = $"接收：已連線（{ep}）";
            TxtHeadConn.Text = $"● 已連接手機  {_phoneName}";
            TxtHeadConn.Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0xFF, 0x88));
            TxtPhone.Text = $"{_phoneName}（{_phoneIp}）";
        });
        _client.Disconnected += _ => Dispatcher.Invoke(() => { TxtNet.Text = "接收：已斷線"; });
        _client.StatsUpdated += s => Dispatcher.Invoke(() =>
        {
            double fps = s.Fps, mbps = s.Mbps;
            if (_useUdp)
            {
                // 影像走 UDP 時 TCP 沒有影像幀，改用 UDP 視窗統計（每秒）
                var nowU = DateTime.UtcNow;
                double secU = Math.Max(0.2, (nowU - _udpWinStart).TotalSeconds);
                fps = _udpWinFrames / secU;
                mbps = _udpWinBytes * 8.0 / secU / 1_000_000.0;
                _udpWinFrames = 0; _udpWinBytes = 0; _udpWinStart = nowU;
            }
            TxtNet.Text = $"接收：{s.Remote}｜{fps:F1}fps｜{mbps:F2}Mbps";
            if (Environment.TickCount64 - _lastNetLogMs > 5000)
            {
                _lastNetLogMs = Environment.TickCount64;
                PLog($"net {s.Fps:F1}fps {s.Mbps:F2}Mbps 累計幀={s.VideoFrames} 丟棄={_drops}");
            }
            try
            {
                if (_useUdp && _udp != null && _client != null)
                {
                    var (loss, jm) = _udp.TakeSecondStats();
                    _ = _client.SendControlAsync("net_state", Cam1Control.NetState(loss, jm));
                }
            }
            catch { }
        });
        _client.ControlReceived += OnNetControl;
        _client.VideoReceived += OnNetVideo;

        TxtNet.Text = $"連線中…{ip}:{port}";
        BtnConnect.IsEnabled = false;
        var (ok, msg) = await _client.ConnectAsync(ip, port);
        TxtNet.Text = (ok ? "接收：" : "") + msg;
        BtnConnect.IsEnabled = true;
        BtnDisconnect.IsEnabled = ok;
        if (!ok) return;

        _connected = true;
        _connMs = Environment.TickCount64;
        _lastUdpMs = 0;
        try
        {
            _udp?.Dispose();
            _useUdp = false;
            _udp = new UdpVideoReceiver();
            _udp.FrameReady += OnUdpVideo;
            _udp.NackNeeded += seqs => { try { _ = _client?.SendControlAsync("nack", Cam1Control.Nack(seqs)); } catch { } };
            _udp.Start();
            _ = _client.SendControlAsync("udp_ready", Cam1Control.UdpReady(GetLanIp(), UdpVideoReceiver.DefaultPort));
        }
        catch (Exception ex) { PLog($"UDP 通道啟動失敗（續用 TCP）：{ex.Message}"); }

        _pumpCts = new CancellationTokenSource();
        _ = Task.Run(() => PreviewPump(_pumpCts.Token));
    }

    private static string GetLanIp()
    {
        try
        {
            string? pick = null, first = null;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("127.")) continue;
                    first ??= ip;
                    if (ip.StartsWith("192.168.")) return ip;
                    if (pick == null && (ip.StartsWith("10.") || ip.StartsWith("172."))) pick = ip;
                }
            }
            return pick ?? first ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _decoder.Stop(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _connected = false;
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _useUdp = false;
        _ffKey = ""; _previewLabel = ""; _firstLoggedFor = "";
        _shown = 0; _netIn = 0; _drops = 0; _dropLoggedFor = "";
        _saving = false;
        TxtLive.Visibility = Visibility.Collapsed;
        CloseDump();
        TxtNet.Text = "接收：未連線";
        TxtHeadConn.Text = "○ 未連接手機";
        TxtHeadConn.Foreground = (Brush)FindResource("MutedBrush");
        TxtPhone.Text = "未連線";
        TxtDecMode.Text = "解碼：－";
        TxtSave.Text = "存檔：關";
        BtnDisconnect.IsEnabled = false;
        ImgPreview.Source = null;
        _lastShownBitmap = null;
        TxtPreview.Text = "等串流…";
        TxtStats.Text = "幀：－";
        StandbyPanel.Visibility = Visibility.Visible;
    }

    // ================= 存檔 / 截圖 =================

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _saving = !_saving;
        if (_saving)
        {
            try
            {
                string dir = CaptureDir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"recv-{DateTime.Now:yyyyMMdd-HHmmss}.h264");
                _dump = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                TxtSave.Text = "存檔：開";
                TxtNet.Text += $"（存到 {Path.GetFileName(path)}）";
            }
            catch (Exception ex) { _saving = false; TxtNet.Text = "存檔打不開：" + ex.Message; }
        }
        else { CloseDump(); TxtSave.Text = "存檔：關"; }
    }

    private static string CaptureDir() => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "_captures");
    private void CloseDump() { try { lock (_lock) { _dump?.Dispose(); _dump = null; } } catch { } }

    private void BtnSnap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var wb = _lastShownBitmap;
            if (wb == null) { TxtPreview.Text = "還沒畫面，截不到"; return; }
            string dir = CaptureDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"snap-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(wb));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            enc.Save(fs);
            TxtPreview.Text = $"已截圖：{Path.GetFileName(path)}";
        }
        catch (Exception ex) { TxtPreview.Text = "截圖失敗：" + ex.Message; }
    }

    // ================= 控制訊息 =================

    private void OnNetControl(string kind, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (kind)
            {
                case "video_params":
                    lock (_lock)
                    {
                        if (root.TryGetProperty("width", out var w)) _vidW = w.GetInt32();
                        if (root.TryGetProperty("height", out var h)) _vidH = h.GetInt32();
                        if (root.TryGetProperty("fps", out var f)) _vidFps = f.GetInt32();
                        if (root.TryGetProperty("rot", out var r)) _vidRot = RotNorm(r.GetInt32());
                        if (root.TryGetProperty("flip", out var fl)) _vidFlip = fl.GetInt32() != 0;
                        if (root.TryGetProperty("codec", out var c)) _vidCodec = c.GetString() == "hevc" ? "hevc" : "h264";
                        PLog($"video_params {_vidW}x{_vidH}@{_vidFps} {_vidCodec} rot={_vidRot} flip={(_vidFlip ? 1 : 0)}");
                    }
                    TryConfigureDecoder();
                    break;

                case "orientation":
                    bool changed = false;
                    lock (_lock)
                    {
                        if (root.TryGetProperty("rot", out var r))
                        {
                            int nr = RotNorm(r.GetInt32());
                            if (nr != _vidRot) { _vidRot = nr; changed = true; }
                        }
                        if (root.TryGetProperty("flip", out var fl))
                        {
                            bool nf = fl.GetInt32() != 0;
                            if (nf != _vidFlip) { _vidFlip = nf; changed = true; }
                        }
                        if (changed) PLog($"方向 rot={_vidRot} flip={(_vidFlip ? 1 : 0)}");
                    }
                    if (changed) TryConfigureDecoder();
                    break;

                case "codec_config":
                    byte[] sps = Convert.FromBase64String(root.GetProperty("sps").GetString()!);
                    byte[] pps = Convert.FromBase64String(root.GetProperty("pps").GetString()!);
                    lock (_lock) { _sps = sps; _pps = pps; }
                    byte[] annexb = IsAnnexB(sps) ? sps : AnnexB.SpsPpsToAnnexB(sps, pps);
                    lock (_lock) { _seqHead = annexb; if (_saving) _dump?.Write(annexb); }
                    TryConfigureDecoder();
                    break;
            }
        }
        catch { }
    }

    private static bool IsAnnexB(byte[] b) =>
        b.Length >= 4 && ((b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 1) ||
                          (b[0] == 0 && b[1] == 0 && b[2] == 1));

    private static int RotNorm(int r)
    {
        int n = ((r % 360) + 360) % 360;
        return (n == 90 || n == 180 || n == 270) ? n : 0;
    }

    private void TryConfigureDecoder(bool force = false)
    {
        string? uiStartText = null, uiFailText = null, uiCamMsg = null;
        lock (_lock)
        {
            if (_vidW <= 0 || _vidH <= 0) return;
            int effRot = _vidRot;
            bool wantHw = !_hwBroken && !File.Exists(@"C:\Temp\ipc-softdec");
            string basePart = $"{_vidCodec}:{_vidW}x{_vidH}:{_vidFps}:{(wantHw ? "hw" : "sw")}";
            string key = $"{basePart}:r{effRot}:f{(_vidFlip ? 1 : 0)}";
            if (key == _ffKey && _decoder.Running) return;
            bool paramOnly = basePart == _ffBase && key != _ffKey;
            if (!force && !paramOnly && _decoder.Running && Environment.TickCount64 - _lastFfStartMs < 2000)
            {
                PLog($"重啟略過（防暴衝）：{key} 沿用 {_ffKey}");
                return;
            }
            _ffKey = key;
            _lastFfStartMs = Environment.TickCount64;
            try { _decoder.Stop(); } catch { }
            string? ff = FfmpegDecoder.FindFfmpeg();
            bool started = ff != null && _decoder.Start(_vidW, _vidH, _vidFps, ff, effRot, _vidFlip, _vidCodec, wantHw);
            if (started)
            {
                _ffBase = basePart;
                PLog($"解碼器啟動：{key}");
                try { if (_seqHead != null) _decoder.Feed(_seqHead); } catch { }
                string facing = _vidFlip ? "前鏡" : "後鏡";
                string portrait = (effRot % 180 != 0) ? "（直向）" : "";
                _previewLabel = $"{_decoder.OutW}x{_decoder.OutH} {facing} rot={effRot}{portrait}・{_decoder.HwMode}";
                uiStartText = $"{_decoder.OutW}x{_decoder.OutH} 解碼器啟動…{facing} rot={effRot}{portrait}・{_decoder.HwMode}";
                _ = _client?.SendControlAsync("idr_request", Cam1Control.IdrRequest());
                // 依串流尺寸/方向重建畫框（直拿→直向畫框、橫拿→橫向畫框，避免黑邊/裁切）
                int ow = _decoder.OutW, oh = _decoder.OutH;
                if ((ow != _camW || oh != _camH) && IsCamTier(ow, oh))
                {
                    var (rok, rmsg) = RestartCamera(ow, oh);
                    uiCamMsg = (rok ? "✅ " : "❌ ") + rmsg + "（OBS 請重加來源）";
                }
            }
            else if (ff == null) uiFailText = "找不到 ffmpeg（C:\\Temp\\ffmpeg）";
        }
        if (uiStartText != null) Dispatcher.Invoke(() => TxtPreview.Text = uiStartText);
        else if (uiFailText != null) Dispatcher.Invoke(() => TxtPreview.Text = uiFailText);
        if (uiCamMsg != null) Dispatcher.Invoke(() => TxtStatus.Text = uiCamMsg);
    }

    // ================= 影像 =================

    private void OnNetVideo(Cam1.Frame frame)
    {
        if (_useUdp) return;
        HandleVideoBytes(frame.Payload);
    }

    private void OnUdpVideo(UdpVideoReceiver.VideoFrame f)
    {
        try
        {
            _lastUdpMs = Environment.TickCount64;
            _udpWinFrames++; _udpWinBytes += f.Avcc.Length;
            if (!_useUdp)
            {
                _useUdp = true;
                PLog("影像來源切換：UDP（TCP 只留控制）");
                try { _ = _client?.SendControlAsync("udp_ok", Cam1Control.UdpOk()); } catch { }
            }
            HandleVideoBytes(f.Avcc);
        }
        catch { }
    }

    private void HandleVideoBytes(byte[] payload)
    {
        byte[]? annexb;
        try { annexb = AnnexB.ConvertAvcc(payload); } catch { annexb = null; }
        if (annexb == null)
        {
            _drops++;
            if (_dropLoggedFor != _ffKey)
            {
                _dropLoggedFor = _ffKey;
                int n = Math.Min(16, payload.Length);
                PLog($"首丟幀 len={payload.Length} hex={BitConverter.ToString(payload, 0, n)}");
            }
            return;
        }
        try
        {
            if ((_netIn++ % 150) == 0) PLog($"vin {_netIn} annexb={annexb.Length}B saving={_saving}");
            if (_saving) lock (_lock) { _dump?.Write(annexb); }
            if (_vidCodec != "hevc" && _seqHead != null && ContainsIdr(annexb)) _decoder.Feed(_seqHead);
            _decoder.Feed(annexb);
        }
        catch { }
    }

    private static bool ContainsIdr(byte[] b)
    {
        for (int i = 0; i + 4 < b.Length; i++)
        {
            int sc;
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) sc = 3;
            else if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1) sc = 4;
            else continue;
            if ((b[i + sc] & 0x1F) == 5) return true;
            i += sc;
        }
        return false;
    }

    private async Task PreviewPump(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_decoder.Running && _ffKey != "")
                {
                    string reason = _decoder.DeathReason;
                    if (_firstLoggedFor != "dead:" + _ffKey)
                    {
                        _firstLoggedFor = "dead:" + _ffKey;
                        PLog($"解碼器死亡：{reason}");
                    }
                    Dispatcher.Invoke(() =>
                    {
                        string msg = "解碼器停了" + (reason != "" ? "：" + reason : "（重連會自動重啟）");
                        if (TxtPreview.Text != msg) TxtPreview.Text = msg;
                    });
                    if (Environment.TickCount64 - _lastFfStartMs > 3000)
                    {
                        if (!_hwBroken && _decoder.HwMode == "硬解" && _decoder.FramesRead == 0 && _decoder.UpMs < 8000)
                        {
                            string err = _decoder.LastErrLines(30).ToLowerInvariant();
                            string[] kws = { "d3d11", "d3d12", "cuda", "hwaccel", "hwdownload", "hardware", "dxva", "qsv", "amf", "cuvid", "nvidia", "gpu" };
                            if (kws.Any(k => err.Contains(k))) { _hwBroken = true; PLog($"硬解不可用，退回軟解：{_decoder.DeathReason}"); }
                        }
                        PLog("死亡自救：重啟解碼器");
                        TryConfigureDecoder();
                    }
                    else await Task.Delay(500, ct);
                    continue;
                }

                byte[]? nv12 = _decoder.TakeLatest();
                if (nv12 == null) { await Task.Delay(15, ct); continue; }

                if (_firstLoggedFor != _ffKey)
                {
                    _firstLoggedFor = _ffKey;
                    PLog($"首幀已解：{_decoder.OutW}x{_decoder.OutH} NV12（{nv12.Length}B）");
                    Dispatcher.Invoke(() =>
                    {
                        TxtLive.Visibility = Visibility.Visible;
                        TxtDecMode.Text = $"解碼：{_decoder.HwMode}";
                        StandbyPanel.Visibility = Visibility.Collapsed;
                    });
                }
                int w = _decoder.OutW, h = _decoder.OutH;
                if (w <= 0 || h <= 0 || (w & 1) != 0 || (h & 1) != 0) continue;
                if (nv12.Length < w * h * 3 / 2) continue;
                // 每個解碼幀都立即餵給虛擬相機（不受顯示節流影響），讓 OBS 拿到最新幀、避免忽新忽舊的閃爍
                try { _frameWriter.Publish(w, h, nv12, SharedFrame.FmtNv12); } catch { }

                if ((DateTime.UtcNow - _lastShown).TotalMilliseconds < 33) continue; // 僅節流畫面顯示
                _lastShown = DateTime.UtcNow;
                _shown++;
                int pw = w, ph = h; // 全解析度預覽（不縮半，維持真實清晰度）
                byte[] small;
                try { small = Nv12Preview.ConvertFull(nv12, w, h); } catch { continue; }
                Dispatcher.Invoke(() =>
                {
                    var target = _useA ? _bitmapA : _bitmapB;
                    _useA = !_useA;
                    if (target == null || target.PixelWidth != pw || target.PixelHeight != ph)
                    {
                        target = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32, null);
                        if (_useA) _bitmapB = target; else _bitmapA = target;
                    }
                    target.WritePixels(new Int32Rect(0, 0, pw, ph), small, pw * 4, 0);
                    ImgPreview.Source = target;
                    _lastShownBitmap = target;
                    string label = _previewLabel != "" ? _previewLabel : $"{w}x{h} 預覽中";
                    if (TxtPreview.Text != label) TxtPreview.Text = label;
                });
                if (Environment.TickCount64 - _lastShownLogMs > 5000)
                {
                    _lastShownLogMs = Environment.TickCount64;
                    if (_connected && !_useUdp && _udp != null
                        && Environment.TickCount64 - _lastFfStartMs > 8000
                        && Environment.TickCount64 - _connMs > 3000
                        && _firstLoggedFor != "udplost:" + _ffKey)
                    {
                        _firstLoggedFor = "udplost:" + _ffKey;
                        PLog("UDP 無幀（被擋？），通知手機續用 TCP");
                        try { _ = _client?.SendControlAsync("udp_lost", Cam1Control.UdpLost()); } catch { }
                    }
                    var st = $"幀 顯示{_shown}／解碼{_decoder.FramesRead} 餵丟{_decoder.FeedDrops} CPU{_decoder.CpuTime.TotalSeconds:F1}s";
                    Dispatcher.Invoke(() => { if (TxtStats.Text != st) TxtStats.Text = st; });
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // ================= 導航 / 分頁 =================

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        HighlightNav(tag);
        if (tag == "Home") { PageHome.Visibility = Visibility.Visible; PageSub.Visibility = Visibility.Collapsed; return; }
        ShowSub(tag);
    }

    private void HighlightNav(string tag)
    {
        foreach (var b in new[] { NavHome, NavSettings, NavAbout })
        {
            bool on = (b.Tag as string) == tag;
            b.Foreground = (Brush)FindResource(on ? "SecondaryBrush" : "MutedBrush");
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void ShowSub(string which)
    {
        PageHome.Visibility = Visibility.Collapsed;
        PageSub.Visibility = Visibility.Visible;
        PageSettings.Visibility = which == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = which == "About" ? Visibility.Visible : Visibility.Collapsed;
        TxtPageTitle.Text = which == "Settings" ? "設定" : "關於";
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag) ShowTab(tag);
    }

    private void ShowTab(string tag)
    {
        PanelDevice.Visibility = tag == "Device" ? Visibility.Visible : Visibility.Collapsed;
        PanelSys.Visibility = tag == "Sys" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (btn, on) in new[] { (TabDevice, tag == "Device"), (TabSys, tag == "Sys") })
        {
            btn.Foreground = (Brush)FindResource(on ? "SecondaryBrush" : "MutedBrush");
            btn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ================= 系統 =================

    private void BtnMax_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ChkSoftDec_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        try
        {
            string flag = @"C:\Temp\ipc-softdec";
            if (ChkSoftDec.IsChecked == true) File.WriteAllText(flag, "soft");
            else File.Delete(flag);
            TxtDecMode.Text = $"解碼：{(ChkSoftDec.IsChecked == true ? "軟解（重連生效）" : "硬解優先")}";
        }
        catch { }
    }

    private async void BtnDiag_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"相機狀態：{TxtStatus.Text}");
        sb.AppendLine($"系統版本：{Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "?")} "
            + $"(DisplayVersion {Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "?")})");
        sb.AppendLine("裝置清單：");
        try
        {
            var devs = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            foreach (var d in devs) sb.AppendLine($"  [{(d.IsEnabled ? "啟用" : "停用")}] {d.Name}");
        }
        catch (Exception ex) { sb.AppendLine("  列舉失敗：" + ex.Message); }
        Clipboard.SetText(sb.ToString());
        TxtStatus.Text = "診斷已複製到剪貼簿";
    }

    // ================= 視窗 / 虛擬相機 =================

    private void Border_Drag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private (bool ok, string msg) StartCamera()
    {
        try
        {
            lock (_lock)
            {
                if (!_mfStarted) { MFFunctions.MFStartup(); _mfStarted = true; }
                _camL ??= CreateCamera(FriendlyLandscape, SourceIdLandscape);
                if (_camL == null) return (false, "建立失敗（Source DLL 有註冊嗎？）");
                if (_camL.Object.Start(null).IsError)
                {
                    try { _camL.Dispose(); } catch { }
                    _camL = null;
                    return (false, "啟動失敗（管理員註冊過了嗎？）");
                }
                return (true, $"虛擬相機已啟動：{FriendlyLandscape}（{_camW}x{_camH}）");
            }
        }
        catch (Exception ex) { return (false, $"啟動發生問題：{ex.Message}"); }
    }

    private static IComObject<IMFVirtualCamera>? CreateCamera(string friendly, string sourceId)
    {
        var hr = Functions.MFCreateVirtualCamera(
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_Session,
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser,
            friendly, sourceId, null, 0, out var camera);
        if (hr.IsError) { PLog($"建立虛擬相機失敗 {friendly}：{hr.Value}"); return null; }
        return new ComObject<IMFVirtualCamera>(camera);
    }

    private (bool ok, string msg) RestartCamera(int w, int h)
    {
        try
        {
            lock (_lock)
            {
                if (w == _camW && h == _camH) return (true, "畫布沒變，不用重建");
                // 寫 HKLM（Source 在 Frame Server/SYSTEM 讀得到；HKCU 看不到）
                try
                {
                    using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\iPhoneCam");
                    k?.SetValue("CamW", w, RegistryValueKind.DWord);
                    k?.SetValue("CamH", h, RegistryValueKind.DWord);
                }
                catch (Exception ex) { return (false, $"寫 HKLM 失敗（請以管理員執行）：{ex.Message}"); }
                try { _camL?.Object.Remove(); } catch { }
                try { _camL?.Dispose(); } catch { }
                _camL = null;
                _camW = w; _camH = h;
            }
            var (ok, msg) = StartCamera();
            if (ok) PLog($"虛擬攝像頭換畫布：{w}x{h}（OBS 請重加來源或開關眼珠）");
            return (ok, msg);
        }
        catch (Exception ex) { return (false, $"重建發生問題：{ex.Message}"); }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _frameWriter.Dispose(); } catch { }
        try { _decoder.Stop(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _beacon?.Dispose(); } catch { }
        CloseDump();
        try
        {
            lock (_lock)
            {
                _camL?.Object.Remove();
                _camL?.Dispose();
                _camL = null;
                if (_mfStarted) MFFunctions.MFShutdown();
            }
        }
        catch { }
        base.OnClosed(e);
    }
}
