using System.Net.Sockets;
using System.Text;

namespace iPhoneCam.Net;

/// <summary>
/// CAM1 主動連線端（SPEC-v4 §11.1 正式路徑：手機監聽 19300、PC 連過去）。
/// 事件形狀與 <see cref="Cam1Server"/> 對稱，UI 可共用同一組 handler。
/// 收訊迴圈一律跑在背景執行緒（下游 Feed 可能阻塞，絕不能占用 UI context）。
/// </summary>
public sealed class Cam1Client : IDisposable
{
    public event Action<string>? Connected;
    public event Action<string>? Disconnected;
    public event Action<Cam1.Frame>? VideoReceived;
    public event Action<string, string>? ControlReceived; // (kind, json)
    public event Action<Cam1Stats>? StatsUpdated;

    private TcpClient? _tcp;
    private CancellationTokenSource? _cts;
    private long _videoFrames, _videoBytes, _controlMsgs;
    private long _windowFrames, _windowBytes;
    private DateTime _windowStart = DateTime.UtcNow;
    private string _remote = "";
    private bool _disposed;

    public bool IsConnected => _tcp?.Connected == true;
    public string Remote => _remote;

    public async Task<(bool ok, string msg)> ConnectAsync(string ip, int port)
    {
        Disconnect();
        _cts = new CancellationTokenSource();
        try
        {
            _tcp = new TcpClient { NoDelay = true }; // 小 NAL 別被 Nagle 拖累（延遲敏感）
            await _tcp.ConnectAsync(ip, port, _cts.Token).ConfigureAwait(false);
            _remote = $"{ip}:{port}";
            _videoFrames = _videoBytes = _controlMsgs = _windowFrames = _windowBytes = 0;
            _windowStart = DateTime.UtcNow;
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
            return (true, $"已連線 {_remote}");
        }
        catch (Exception e)
        {
            Disconnect();
            return (false, "連不上：" + e.Message);
        }
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Close(); } catch { }
        _tcp = null;
    }

    /// <summary>發控制訊息（UI 執行緒可安全呼叫，如 idr_request）。</summary>
    public async Task SendControlAsync(string kind, object message)
    {
        var tcp = _tcp;
        if (tcp == null || !tcp.Connected) return;
        try { await Cam1.SendControlAsync(tcp.GetStream(), kind, message, CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        NetworkStream stream;
        try { stream = _tcp!.GetStream(); }
        catch { return; }
        Connected?.Invoke(_remote);
        try
        {
            // 交握：主動送 hello（手機是伺服器，但雙方都報身分）
            await Cam1.SendControlAsync(stream, "hello",
                Cam1Control.Hello(Environment.MachineName), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested && _tcp?.Connected == true)
            {
                var frame = await Cam1.ReceiveAsync(stream, ct).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case Cam1.TypeVideo:
                        _videoFrames++; _videoBytes += frame.Payload.Length;
                        _windowFrames++; _windowBytes += frame.Payload.Length;
                        VideoReceived?.Invoke(frame);
                        break;
                    case Cam1.TypeControl:
                        _controlMsgs++;
                        string json = Encoding.UTF8.GetString(frame.Payload);
                        ControlReceived?.Invoke(Cam1.ControlKind(json), json);
                        break;
                }
                TickStats();
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        Disconnected?.Invoke(_remote);
    }

    private void TickStats()
    {
        var now = DateTime.UtcNow;
        double sec = (now - _windowStart).TotalSeconds;
        if (sec < 1.0) return;
        var snap = new Cam1Stats(_remote, _videoFrames, _videoBytes, _controlMsgs,
            _windowFrames / sec, _windowBytes * 8.0 / sec / 1_000_000.0);
        _windowFrames = 0; _windowBytes = 0; _windowStart = now;
        StatsUpdated?.Invoke(snap);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _cts?.Dispose();
    }
}
