using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace iPhoneCam.Net;

/// <summary>
/// CAM1 收流端（SPEC-v3 §3/§4）：監聽 19300，交握＋碼率擋板＋統計。
/// P2 的舊路徑（PC 監聽、手機連入），正式路徑是 <see cref="Cam1Client"/>；
/// 保留本類別供迴路測試與假客戶端（testsrc）驗證整條鏈。
/// </summary>
public sealed class Cam1Server : IDisposable
{
    public event Action<string>? ClientConnected;
    public event Action<string>? ClientDisconnected;
    public event Action<Cam1.Frame>? VideoReceived;
    public event Action<string, string>? ControlReceived; // (kind, json)
    public event Action<Cam1Stats>? StatsUpdated;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Session? _session;

    public bool Running => _listener != null;

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Cam1.Port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _session?.Close(); } catch { }
        _session = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _session?.Close();
                _session = new Session(client, this);
                _ = _session.RunAsync();
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        }
    }

    public void Dispose() => Stop();

    private sealed class Session(TcpClient client, Cam1Server owner)
    {
        private readonly TcpClient _client = client;
        private long _videoFrames, _videoBytes, _controlMsgs;
        private long _windowFrames, _windowBytes;
        private DateTime _windowStart = DateTime.UtcNow;
        private double _fps, _mbps;

        public void Close()
        {
            try { _client.Close(); } catch { }
        }

        private string Remote()
        {
            try { return _client.Client.RemoteEndPoint?.ToString() ?? "unknown"; }
            catch { return "unknown"; }
        }

        public async Task RunAsync()
        {
            string remote = Remote();
            owner.ClientConnected?.Invoke(remote);
            try
            {
                using var stream = _client.GetStream();
                // 交握：主動回 hello（PC＝大腦，見 SPEC-v3 §1）
                await Cam1.SendControlAsync(stream, "hello",
                    Cam1Control.Hello(Environment.MachineName), CancellationToken.None).ConfigureAwait(false);
                while (_client.Connected)
                {
                    var frame = await Cam1.ReceiveAsync(stream, CancellationToken.None).ConfigureAwait(false);
                    switch (frame.Type)
                    {
                        case Cam1.TypeVideo:
                            _videoFrames++; _videoBytes += frame.Payload.Length;
                            _windowFrames++; _windowBytes += frame.Payload.Length;
                            owner.VideoReceived?.Invoke(frame);
                            break;
                        case Cam1.TypeControl:
                            _controlMsgs++;
                            string json = Encoding.UTF8.GetString(frame.Payload);
                            string kind = Cam1.ControlKind(json);
                            if (kind == "link_info") await ReplyLinkCapAsync(stream, json).ConfigureAwait(false);
                            else owner.ControlReceived?.Invoke(kind, json);
                            break;
                    }
                    TickStats(remote);
                }
            }
            catch { }
            owner.ClientDisconnected?.Invoke(remote);
        }

        private void TickStats(string remote)
        {
            var now = DateTime.UtcNow;
            double sec = (now - _windowStart).TotalSeconds;
            if (sec < 1.0) return;
            _fps = _windowFrames / sec;
            _mbps = _windowBytes * 8.0 / sec / 1_000_000.0;
            _windowFrames = 0; _windowBytes = 0; _windowStart = now;
            owner.StatsUpdated?.Invoke(new Cam1Stats(remote, _videoFrames, _videoBytes, _controlMsgs, _fps, _mbps));
        }

        // 碼率擋板（SPEC-v3 §3）：wifi 100／usb2 100／usb3 400，取雙方最低
        private static async Task ReplyLinkCapAsync(NetworkStream stream, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string transport = root.TryGetProperty("transport", out var t) ? t.GetString() ?? "wifi" : "wifi";
                double measured = root.TryGetProperty("measured_mbps", out var m) ? m.GetDouble() : 0;
                double cap = transport switch { "usb3" => 400, "usb2" => 100, _ => 100 };
                double allowed = Math.Min(cap, measured > 0 ? measured : cap);
                await Cam1.SendControlAsync(stream, "link_cap",
                    new { transport, cap_mbps = cap, allowed_mbps = allowed }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { }
        }
    }
}
