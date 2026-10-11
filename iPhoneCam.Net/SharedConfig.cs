using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace iPhoneCam.Net;

/// <summary>
/// App → Source DLL 的畫布尺寸通道（跨帳號可見）。
/// 關鍵：Source DLL 跑在 Frame Server 服務（SYSTEM），**看不到使用者的 HKCU**，
/// 所以畫布尺寸不能放登錄檔；改放 Global 共用記憶體（Everyone 可讀）。
/// 版面（64B）：landW i32, landH i32, portW i32, portH i32。
/// </summary>
public static class SharedConfig
{
    public const string MapName = @"Global\iPhoneCam.Config";
    public const int Size = 64;
    public const int OffLandW = 0;
    public const int OffLandH = 4;
    public const int OffPortW = 8;
    public const int OffPortH = 12;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint sd, out uint len);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileMapping(nint hFile, ref SECURITY_ATTRIBUTES sa, uint protect, uint sizeHi, uint sizeLo, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint MapViewOfFile(nint hMap, uint access, uint offHi, uint offLo, nint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(nint p);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint h);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public nint lpSecurityDescriptor; public int bInheritHandle; }

    private static nint _mapHandle;
    private static nint _view;
    private static readonly object _lock = new();

    /// <summary>App 端：建立/開啟並寫入畫布尺寸（Global、Everyone 可讀）。</summary>
    public static bool Write(int landW, int landH, int portW, int portH)
    {
        try
        {
            lock (_lock)
            {
                if (_view == 0)
                {
                    nint sd = 0;
                    try
                    {
                        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;WD)", 1, out sd, out _)) return false;
                        var sa = new SECURITY_ATTRIBUTES
                        {
                            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                            lpSecurityDescriptor = sd,
                            bInheritHandle = 0
                        };
                        const uint PAGE_READWRITE = 4;
                        _mapHandle = CreateFileMapping(new nint(-1), ref sa, PAGE_READWRITE, 0, Size, MapName);
                    }
                    finally { if (sd != 0) { try { LocalFree(sd); } catch { } } }
                    if (_mapHandle == 0) return false;
                    _view = MapViewOfFile(_mapHandle, 0xF001F, 0, 0, Size); // FILE_MAP_ALL_ACCESS
                    if (_view == 0) return false;
                }
                Marshal.WriteInt32(_view, OffLandW, landW);
                Marshal.WriteInt32(_view, OffLandH, landH);
                Marshal.WriteInt32(_view, OffPortW, portW);
                Marshal.WriteInt32(_view, OffPortH, portH);
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>Source 端：讀取畫布尺寸（依橫/直）。讀不到回 false。</summary>
    public static bool Read(bool portrait, out int w, out int h)
    {
        w = 0; h = 0;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var acc = mmf.CreateViewAccessor(0, Size, MemoryMappedFileAccess.Read);
            w = acc.ReadInt32(portrait ? OffPortW : OffLandW);
            h = acc.ReadInt32(portrait ? OffPortH : OffLandH);
            return w > 0 && h > 0;
        }
        catch { return false; }
    }
}
