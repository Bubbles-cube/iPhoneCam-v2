---
name: mf-virtualcam-debug
description: Use when debugging Windows Media Foundation virtual camera image problems in this repo: OBS stripes/flicker/frozen frames, NV12 stride/pitch mismatch, shared-memory frame delivery, canvas size mismatch, or virtual camera registration issues. Covers iPhoneCam.Source, iPhoneCam.Net SharedFrame, and OBS verification steps.
---

# MF 虛擬相機影像除錯

本 repo（iPhoneCam）的虛擬相機鏈：手機 → `iPhoneCam.Net`（解碼）→ `SharedFrame`（MMF 跨進程）→ `iPhoneCam.Source`（MF `IMFMediaSource`，跑在 FrameServer/SYSTEM）→ OBS。PC App 預覽正常時，上游鏈沒問題，**只查 DLL→OBS 交界**。

## 黃金法則（先查這些，不猜）

1. 交給下游的 buffer 一律 **packed（pitch＝寬）**，宣告（`MF_MT_DEFAULT_STRIDE`）與實際一致；**永遠不要假設 allocator pitch＝寬**。
2. 要轉拷貝就用 `Lock2DSize` 查實際 pitch 後逐列拷（或 `MFCopyImage`）；禁止整塊 `Marshal.Copy` 進 allocator buffer。
3. 跨進程餵幀用**三重緩衝＋原子 writeIdx**（仿 OBS `shared-memory-queue`），不用 mutex；寫端寫 `slot[(idx+1)%3]` 並發布同一索引，初始 `idx=0` 視為無幀。
4. sample duration 不可寫死：用實測到達間隔（10～100ms 箝制），否則 60fps 會抖／卡住。
5. **診斷先行再改**：`C:\Temp\ipcdll.log` 內 `GEN`（走哪分支）／`START`（協商尺寸 vs 畫布）／`HB packed/row/linear/skip`（哪條投遞路在跑）／`2D pitch=`（allocator 實際 pitch）。
6. OBS 換格式／換 DLL 後必須「**刪除來源重加**」，否則看到的是快取的舊格式。
7. 改 Source DLL 前先**停 FrameServer＋關 App**（需管理員，否則檔案被鎖）；新增 CLSID 需管理員 `regsvr32`（見 `iPhoneCam.Source/register.bat`）。
8. Source 跑在 SYSTEM，**看不到使用者 HKCU**——畫布尺寸走 `HKLM\SOFTWARE\iPhoneCam`（App 需管理員 manifest 寫入）。

## 已驗證的關鍵事實（別再踩）

- allocator 實際 pitch 有 **64-byte padding**（1080→1088、1440→1472）；用 `Lock2DSize` 查，不要用猜的。
- 上游 smourier 原版 CPU 路徑**從不 raw copy**（走 `CColorConvertDMO` 讓 MFT 自己寫）；GPU 路徑 `RemoveAllBuffers＋AddBuffer` 自有 buffer。本專案定稿：`FrameGenerator.DeliverNv12Frame` 優先自建 packed buffer 投遞，退路才逐列拷貝。
- OBS 的 MF 擷取路徑對 linesize 極敏感（見 OBS `win-mediafoundation` PR #10471 Elgato 案例）；宣告與實際差一個 byte 就是條紋。
- FrameServer 會鎖住 DLL：`Stop-Service FrameServer -Force`（管理員）後才能重建。

## 關鍵檔案

- 投遞：`iPhoneCam.Source/FrameGenerator.cs`（`DeliverNv12Frame`、`TryWriteNv12To2D`）
- 宣告：`iPhoneCam.Source/MediaStream.cs`（media type、stride、duration）、`Shared.cs`（`CameraConfig.ReadSize`、CLSID）
- 通道：`iPhoneCam.Net/SharedFrame.cs`（v2 三重緩衝）、`iPhoneCam.Net/SharedConfig.cs`
- App 端：`iPhoneCam.App/MainWindow.xaml.cs`（`Publish`、`RestartCamera` 寫 HKLM）
- 完整實錄：`AGENTS.md` §11（含症狀→原因→修正對照表與參考實作清單）
