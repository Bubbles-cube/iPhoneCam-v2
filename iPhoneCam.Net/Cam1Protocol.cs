using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace iPhoneCam.Net;

/// <summary>
/// CAM1 訊框格式（SPEC-v3 §4，凍結）：
/// [MAGIC "CAM1" 4B][型別 1B][時間戳微秒 8B][長度 4B][酬載]，全部大端序。
/// 型別 <see cref="TypeVideo"/>＝影像（H.264/HEVC，Annex-B，SPS/PPS 帶內）、
/// <see cref="TypeControl"/>＝控制（UTF-8 JSON，含 kind）。
/// 分工：手機是伺服器，PC 主動連線；控制走 TCP，影像可走 UDP（見 UdpVideoReceiver）。
/// </summary>
public static class Cam1
{
    // 埠（凍結）：影像/控制 TCP、UDP 影像、UDP 信標。
    public const int Port = 19300;
    public const int UdpVideoPort = 19302;
    public const int BeaconPort = 19301;

    public const uint Magic = 0x43414D31; // "CAM1"
    public const byte TypeVideo = 0x01;
    public const byte TypeControl = 0x03; // 0x02 保留（純視訊，不做音訊）

    public const int HeaderSize = 4 + 1 + 8 + 4;
    public const int MaxPayload = 16 * 1024 * 1024;

    public sealed record Frame(byte Type, long TimestampMicros, byte[] Payload);

    public static long NowMicros() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

    public static async Task SendAsync(NetworkStream stream, byte type, long timestampMicros,
        byte[] payload, CancellationToken ct)
    {
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), Magic);
        header[4] = type;
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(5, 8), timestampMicros);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(13, 4), payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>送控制訊息：把 kind 併入 JSON 物件再送。</summary>
    public static async Task SendControlAsync(NetworkStream stream, string kind, object message,
        CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(message)?.AsObject() ?? new JsonObject();
        node["kind"] = kind;
        await SendAsync(stream, TypeControl, NowMicros(), Encoding.UTF8.GetBytes(node.ToJsonString()), ct)
            .ConfigureAwait(false);
    }

    public static async Task<Frame> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = await ReadExactAsync(stream, HeaderSize, ct).ConfigureAwait(false);
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        if (magic != Magic) throw new InvalidDataException($"MAGIC 不符：0x{magic:X8}");
        byte type = header[4];
        long ts = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(5, 8));
        int len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(13, 4));
        if (len < 0 || len > MaxPayload) throw new InvalidDataException($"長度不合理：{len}");
        byte[] payload = len == 0 ? [] : await ReadExactAsync(stream, len, ct).ConfigureAwait(false);
        return new Frame(type, ts, payload);
    }

    public static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        byte[] buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(got, count - got), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("對端已斷線");
            got += n;
        }
        return buf;
    }

    public static string ControlKind(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>把 JSON 尾端多餘的 kind 拿掉後取屬性（手機送來的 JSON 我們只讀需要的欄位）。</summary>
    public static bool TryGet(JsonElement root, string name, out JsonElement value)
        => root.TryGetProperty(name, out value);
}

/// <summary>PC → 手機 的控制訊息酬載（欄位名照 SPEC-v3 §4）。</summary>
public static class Cam1Control
{
    public static object Hello(string device) =>
        new { app = "iPhoneCam-PC", version = "v4", device };

    public static object SetVideo(int width, int height, int fps, string codec, double bitrateMbps) =>
        new { width, height, fps, codec, bitrate_mbps = bitrateMbps };

    public static object SetImage(object image) => new { image };

    public static object IdrRequest() => new { };

    public static object UdpReady(string host, int port) => new { host, port };

    public static object UdpOk() => new { };

    public static object UdpLost() => new { };

    public static object Nack(int[] seqs) => new { seqs };

    public static object NetState(double loss, int jitterMs) => new { loss, jitterMs };
}
