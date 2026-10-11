namespace VCamNetSampleSource
{
    public static class Shared
    {
        public const string CLSID_VCamNet = "8136b935-e87f-4c31-9456-31eedfd84776"; // 橫向虛擬鏡頭
        public const string CLSID_VCamNetPortrait = "7e5c1a20-9b34-4d61-8f22-3c6a9b0d1e54"; // 直向虛擬鏡頭
    }

    /// <summary>
    /// 虛擬攝像頭畫布尺寸：跟著串流走（App 寫 HKCU，DLL 啟動時讀）。
    /// 只吃檔位表內尺寸（其它一律回 720p），payload 上限 4K 以內。
    /// </summary>
    public static class CameraConfig
    {
        public const string RegPath = @"SOFTWARE\iPhoneCam";
        public static readonly (int w, int h)[] Tiers =
        {
            (960, 720), (1440, 1080), (1920, 1440), (2880, 2160),
            (720, 960), (1080, 1440), (1440, 1920), (2160, 2880),
        };

        public static (int w, int h) ReadSize(bool portrait = false)
        {
            // 讀 HKLM（App 以管理員寫入；Frame Server/SYSTEM 讀得到）。畫布＝串流實際尺寸（依方向）。
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegPath);
                if (k != null)
                {
                    object? w = k.GetValue("CamW"), h = k.GetValue("CamH");
                    if (w is int wi && h is int hi && wi > 0 && hi > 0)
                        return (wi, hi);
                }
            }
            catch { }
            return portrait ? (1080, 1920) : (1920, 1080);
        }

        public static bool IsTier(int w, int h)
        {
            foreach (var t in Tiers)
                if (t.w == w && t.h == h) return true;
            return false;
        }
    }
}
