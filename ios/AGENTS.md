# iOS UI 模板

修改 iOS UI、相機預覽或建置設定前，讀本目錄 `README.md` 的建置與驗證範圍。此子目錄目前是獨立 SwiftUI shell；所有美顏數字僅是預設參數，串流與桌面輸出尚未接入。

原生 Liquid Glass API 保留 iOS 26 availability guard，較舊版本使用標準元件。相機設定與啟動在同一 serial queue 執行，先 `commitConfiguration()` 才 `startRunning()`。

實作傳輸前先讀根目錄 `SPEC-v3.md`，遵守既有 CAM1/CUM1 規格。UI 模板建置或 Simulator 畫面不構成真機、USB、美顏或 virtual camera 驗收。
