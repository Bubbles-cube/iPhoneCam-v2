# iPhoneCam v2 — 專案記憶與設計藍圖

> 本 repo（`Bubbles-cube/iPhoneCam-v2`）是 **泡泡直播助手／iPhoneCam v2**：
> 手機鏡頭 → PC 虛擬攝影機，裝好後 OBS／Discord／Teams 直接選用。
> v2 由 v1 從頭改寫（Android 端 Kotlin、PC 端 .NET 10 + WPF、Media Foundation 虛擬攝影機）；
> v1（`iPhoneCam-v1`）已封存凍結，不再開發。
>
> 本專案自身規格以 `SPEC-v3.md` 為準；產品簡介見 `README.md`。
> 動手前先讀 `SPEC-v3.md`，再用下方機制對照表選路。

## v2 現況（2026-10-11）

### 元件與角色

| 元件 | 技術 | 角色 |
|---|---|---|
| `AndroidSender/` | Kotlin、Camera2、MediaCodec | 手機：擷取＋硬編 H.264／HEVC；TCP 伺服器＋UDP 信標／影像傳送 |
| `iPhoneCam.Net/` | C#、net10.0（無 UI 相依） | PC：協定／解碼／共享記憶體／統計 |
| `iPhoneCam.App/` | C#、WPF（.NET 10） | PC 控制台：裝置清單、預覽、參數、統計、霓虹 UI |
| `iPhoneCam.Source/` | C#、Media Foundation COM DLL | PC 虛擬攝影機（`MFCreateVirtualCamera`＋`IMFMediaSource`，輸出 NV12） |

### 凍結常數

- TCP 控制 **19300**（手機監聽、PC 主動連入）；UDP 信標 **19301**；UDP 影像（CUM1）**19302**。
- CAM1 訊框：`[MAGIC "CAM1" 4B][型別 1B][tsUs 8B][len 4B]`；型別 `0x01` 影像、`0x03` 控制。
- CUM1 分片：`[0x43554D31][seq u32][tsUs u64][flags 1B][fragIdx u16][fragTotal u16][payload ≤1400B]`。
- 共享記憶體：影格 `Global\iPhoneCam.Frame.v1`（BGRA／NV12）；畫布尺寸 `Global\iPhoneCam.Config`。
- 虛擬攝影機 CLSID `{8136b935-e87f-4c31-9456-31eedfd84776}`。
- 檔位（4:3 滿幅）：960×720／1440×1080／1920×1440／2880×2160；FPS 30／60；碼率 4／8／16／32 Mbps；2K 以上 HEVC。

### 開發注意（地雷，先查再改）

1. 交給下游的 buffer 一律 **packed（pitch＝寬）**，宣告與實際一致；**永不假設 allocator pitch＝寬**。
2. 跨處理程序餵幀用**多重緩衝＋原子索引**，不用 mutex；斷訊才凍結上一幀。
3. 手機／PC 都跑 **4:3 原生、CMOS 滿幅不裁切**；方向不重啟編碼，維持 sensor 原生方向，PC 以 `-vf` 轉正。
4. Source DLL 跑在 FrameServer（SYSTEM），**看不到使用者 HKCU** → 畫布尺寸走 `Global\iPhoneCam.Config`（`SharedConfig.cs`），不是登錄檔。
5. session 失敗要能逐檔降（4K→2K→1080P→720P→30fps）；drain 執行緒要防重入（`encGen`）。
6. 診斷日誌：預覽 `C:\Temp\ipc-preview.log`、DLL `C:\Temp\ipcdll.log`。

---

## 附錄：虛擬相機影像除錯實錄（2026-10-11：OBS 閃爍／條紋／卡住）

> 背景：PC App 自己的預覽一直正常，但 OBS 端條紋、閃爍、甚至定住。來回多次才修好。
> 一句話教訓：**PC 預覽正常＝上游鏈（手機→網路→解碼）正常，問題只剩 DLL→OBS 交界**（buffer 版面、宣告、時間戳）。

### 症狀 → 真正原因 → 修正

| 症狀 | 真正原因 | 修正（定稿） |
|---|---|---|
| OBS 閃爍（忽新忽舊） | App→Source 共用記憶體是「單一 buffer＋mutex」：寫端搶不到鎖就跳幀 | `SharedFrame` v2：**三重緩衝＋原子 writeIdx、無鎖**（仿 OBS `shared-memory-queue`） |
| 畫面像被裁切／直向全雜訊 | writeIdx/slot 索引錯位：寫 `slot[舊idx]` 卻發布 `舊idx+1`，讀端讀到未寫入的 slot | 寫入 `slot[(idx+1)%3]` 並發布同一索引；初始 `idx=0` 視為無幀 |
| 畫布永遠 960×720（來源 1440×1080 被縮小） | Source 跑在 FrameServer（SYSTEM），**看不到使用者 HKCU**，registry 永遠讀不到；Global MMF 又因一般使用者無權建立、`HKLM` 需管理員 | App 加管理員 manifest，把畫布寫 `HKLM\SOFTWARE\iPhoneCam`；方向變就重建畫框（OBS 需重加來源） |
| 直向條紋 | allocator 實際 pitch 有 **64-byte padding**（1080→1088、1440→1472），整塊拷貝假設 pitch＝寬 | `Lock2DSize` 查實際 pitch，逐列拷貝（或 `MFCopyImage`，同等） |
| 修完拷貝還是條紋 | 只修拷貝不夠：**宣告的 stride 也必須跟實際一致**，且 OBS 會快取舊格式 | 宣告 `stride＝寬`；交付的 buffer 一律 packed（見下） |
| 橫向顯示一下就卡住 | sample duration 寫死 33ms，手機 60fps 時抖動 | duration 改用**實測到達間隔**（10～100ms 箝制） |
| 最終定稿（核心） | 下游對 stride 的解讀各異（看／不看宣告），唯一共同語言是 **packed** | `DeliverNv12Frame`：優先自建 packed `MFCreateMemoryBuffer`＋`RemoveAllBuffers/AddBuffer`（跟上游 GPU 路徑同款手法），退路才用 2D 逐列拷貝 |

### 黃金法則（以後影像問題先查這些，不猜）

1. 交給下游的 buffer 一律 **packed（pitch＝寬）**，宣告與實際一致；**永遠不要假設 allocator pitch＝寬**。
2. 真要轉拷貝，必須 `Lock2D` 查實際 pitch 逐列拷（或 `MFCopyImage`），禁止整塊 `Marshal.Copy`。
3. 跨進程餵幀用**多重緩衝＋原子索引**，不用 mutex（寫端永不阻塞）。
4. **診斷先行再改**：`GEN`（走哪分支）／`START`（協商尺寸 vs 畫布）／`HB packed/row/linear/skip`（哪條路在跑）／`2D pitch=`（實際 pitch）——全在 `C:\Temp\ipcdll.log`。
5. OBS 換格式／換 DLL 後必須「**刪除來源重加**」，否則看到的是快取的舊格式。
6. 改 Source DLL 前先**停 FrameServer＋關 App**（需管理員）；新增 CLSID 需管理員 `regsvr32`。
7. 懷疑方向時：PC 預覽正常＝上游沒問題，收斂到 DLL→OBS 交界即可。

### 驗證過的參考寫法（皆為實際比對過的開源實作）

- **上游 smourier VCamNetSample**：CPU 路徑**從不 raw copy**（RGB→WIC→`CColorConvertDMO` 讓 MFT 自己寫 allocator）；GPU 路徑 `RemoveAllBuffers＋AddBuffer` 自有 buffer。
- **OBS `win-dshow/virtualcam-filter.cpp`**：triple-buffer 共享記憶體＋專用 scaler 重排＋明確時間戳 `UnlockSampleData(ts, ts+interval)`。
- **微軟 `Windows-Camera` VirtualCamera 範例**：NV12 用 `MFCreate2DMediaBuffer`；pull model。
- **OBS `win-mediafoundation` PR #10471**：MF 路徑對 linesize 極敏感（Elgato 曾因此 crash）。
- **ReisProduction/Wincam、locosmac/virtual-camera、WinCamHTTP**：MF 虛擬相機＋IPC（pipe）＋每相機獨立 CLSID 架構可行。
- **反例**：本專案舊版「P2d v3 NV12 直通整塊拷貝」是所有參考裡唯一直接往 allocator 倒 packed bytes 的——正是條紋來源。
