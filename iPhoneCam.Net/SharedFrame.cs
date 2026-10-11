using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace iPhoneCam.Net;

/// <summary>
/// App → 虛擬攝像頭 Source DLL 的跨處理程序影格通道（v2）。
/// 參考 OBS win-dshow 的 shared-memory-queue：**三重緩衝 + 原子 writeIdx，不用 mutex**。
/// 寫端永遠寫「下一個」slot 並最後才發布 writeIdx；讀端只讀 writeIdx 指向的「已完成」slot，
/// 因此不會阻塞、不會讀到半寫入的幀、也不會忽新忽舊（消除閃爍）。
/// Global 命名空間（FrameServer 以 SYSTEM 執行仍可存取）。
/// </summary>
public static class SharedFrame
{
    public const string MapName = @"Global\iPhoneCam.Frame.v2";
    public const uint Magic = 0x43414D47; // "CAMG"
    public const int GlobalHeaderSize = 64;
    public const int SlotHeaderSize = 64;
    public const int SlotCount = 3;
    public const int FmtBgra = 0, FmtNv12 = 1;
    public const int MaxW = 3840, MaxH = 2160;
    public const int MaxPayload = MaxW * MaxH * 4;
    public const int SlotSize = SlotHeaderSize + MaxPayload;
    public static readonly long MapSize = GlobalHeaderSize + (long)SlotCount * SlotSize;
    public const int FreshMs = 500;

    // global header
    public const int G_Magic = 0;     // u32
    public const int G_WriteIdx = 8;  // i64（最新完成的幀索引；原子發布）
    // slot header（相對 slot 起點）
    public const int S_W = 0;         // i32
    public const int S_H = 4;         // i32
    public const int S_Fmt = 8;       // i32
    public const int S_Idx = 16;      // i64（此 slot 的 writeIdx）

    public static int SlotOffset(int i) => GlobalHeaderSize + i * SlotSize;
    public static int PayloadSize(int w, int h, int fmt) => fmt == FmtNv12 ? w * h * 3 / 2 : w * h * 4;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint sd, out uint len);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileMapping(nint hFile, ref SECURITY_ATTRIBUTES sa, uint protect, uint sizeHi, uint sizeLo, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint h);

    internal static string LastCreateError = "";

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public nint lpSecurityDescriptor; public int bInheritHandle; }

    /// <summary>讀端（DLL，通常有權限）建立 Everyone-full 的 Global MMF；handle 需一直持有。</summary>
    internal static nint CreateGlobalMap()
    {
        nint sd = 0;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;WD)", 1, out sd, out _))
            {
                LastCreateError = "sddl:" + Marshal.GetLastWin32Error();
                return 0;
            }
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = 0
            };
            const uint PAGE_READWRITE = 4;
            long size = MapSize;
            nint map = CreateFileMapping(new nint(-1), ref sa, PAGE_READWRITE,
                (uint)(size >> 32), (uint)(size & 0xFFFFFFFF), MapName);
            if (map == 0) LastCreateError = "create:" + Marshal.GetLastWin32Error();
            return map; // 呼叫端留著
        }
        catch (Exception e) { LastCreateError = "ex:" + e.GetType().Name; return 0; }
        finally { if (sd != 0) { try { LocalFree(sd); } catch { } } }
    }
}

/// <summary>寫端（App）。三重緩衝、無鎖；滿載時只是覆寫較舊的 slot，永不阻塞。</summary>
public sealed class SharedFrameWriter : IDisposable
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private bool _disposed;
    public static string LastOpenError = "";

    private bool Ensure()
    {
        if (_acc != null) return true;
        try
        {
            // 只開不建：沒消費者（DLL 還沒建 Global）就跳過，不浪費
            _mmf = MemoryMappedFile.OpenExisting(SharedFrame.MapName, MemoryMappedFileRights.ReadWrite);
            _acc = _mmf.CreateViewAccessor();
            return true;
        }
        catch (Exception e) { LastOpenError = e.GetType().Name + ":" + e.Message; return false; }
    }

    public bool Publish(int w, int h, byte[] pixels, int fmt = SharedFrame.FmtBgra)
    {
        try
        {
            if (w <= 0 || h <= 0 || w > SharedFrame.MaxW || h > SharedFrame.MaxH) return false;
            if ((w & 1) != 0 || (h & 1) != 0) return false;
            int n = SharedFrame.PayloadSize(w, h, fmt);
            if (pixels.Length < n) return false;
            if (!Ensure() || _acc == null) return false;

            long n2 = _acc.ReadInt64(SharedFrame.G_WriteIdx) + 1; // 本次的幀索引
            int slot = (int)(n2 % SharedFrame.SlotCount);
            int off = SharedFrame.SlotOffset(slot);

            // 先寫入 slot 內容，最後才發布 writeIdx（讀端只會讀到完成的 slot）
            _acc.Write(off + SharedFrame.S_W, w);
            _acc.Write(off + SharedFrame.S_H, h);
            _acc.Write(off + SharedFrame.S_Fmt, fmt);
            _acc.Write(off + SharedFrame.S_Idx, n2);
            _acc.WriteArray(off + SharedFrame.SlotHeaderSize, pixels, 0, n);
            _acc.Write(SharedFrame.G_WriteIdx, n2);
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _acc?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        _acc = null; _mmf = null;
    }
}

/// <summary>讀端（Source DLL）。每次取「最新完成的 slot」；fresh＝500ms 內有新幀。</summary>
public sealed class SharedFrameReader
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private long _lastIdx = -1;
    private long _lastLocalMs;
    private byte[]? _buf;
    private int _w, _h, _fmt;
    private static nint _mapHandle; // 建完一直持有，放掉 Global 物件就消失
    public static string LastReaderError = "";

    /// <summary>目前緩衝的格式（FmtBgra/FmtNv12）。</summary>
    public int Format => _fmt;

    private bool Ensure()
    {
        if (_acc != null) return true;
        try
        {
            if (_mapHandle == 0)
            {
                _mapHandle = SharedFrame.CreateGlobalMap();
                if (_mapHandle == 0) { LastReaderError = "create:" + SharedFrame.LastCreateError; return false; }
            }
            _mmf = MemoryMappedFile.OpenExisting(SharedFrame.MapName, MemoryMappedFileRights.ReadWrite);
            _acc = _mmf.CreateViewAccessor();
            LastReaderError = "";
            return true;
        }
        catch (Exception e) { LastReaderError = "open:" + e.GetType().Name + ":" + e.Message; return false; }
    }

    public bool TryGetLatest(out int w, out int h, out byte[]? data, out bool fresh)
    {
        w = _w; h = _h; data = _buf; fresh = false;
        try
        {
            if (!Ensure() || _acc == null) return _buf != null;
            long widx = _acc.ReadInt64(SharedFrame.G_WriteIdx);
            if (widx != _lastIdx && widx != 0)
            {
                int slot = (int)(widx % SharedFrame.SlotCount);
                int off = SharedFrame.SlotOffset(slot);
                int sw = _acc.ReadInt32(off + SharedFrame.S_W);
                int sh = _acc.ReadInt32(off + SharedFrame.S_H);
                int fmt = _acc.ReadInt32(off + SharedFrame.S_Fmt);
                if (fmt != SharedFrame.FmtBgra && fmt != SharedFrame.FmtNv12) fmt = SharedFrame.FmtBgra;
                if (sw > 0 && sh > 0 && sw <= SharedFrame.MaxW && sh <= SharedFrame.MaxH)
                {
                    int n = SharedFrame.PayloadSize(sw, sh, fmt);
                    if (_buf == null || _buf.Length < n) _buf = new byte[n];
                    int read = _acc.ReadArray(off + SharedFrame.SlotHeaderSize, _buf, 0, n);
                    if (read == n) { _w = sw; _h = sh; _fmt = fmt; _lastIdx = widx; _lastLocalMs = Environment.TickCount64; }
                }
            }
        }
        catch { }
        w = _w; h = _h; data = _buf;
        fresh = _buf != null && (Environment.TickCount64 - _lastLocalMs) <= SharedFrame.FreshMs;
        return _buf != null;
    }
}
