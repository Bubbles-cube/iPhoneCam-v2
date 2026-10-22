# Phone Lens：原生 iOS UI 模板

這是獨立的 SwiftUI UI shell，加入 iPhoneCam-v2 作為 iOS 設計起點，不更動既有 Android／Windows 程式或 CAM1/CUM1 協定。

## 已提供

- 炭黑與低飽和綠的取景器版面、繁體中文文案及 SF Symbols。
- iOS 26+ 原生 `.glass`、`.glassProminent`、`GlassEffectContainer`；iOS 17–25 採標準 bordered 樣式。
- 系統 sheet、三項美顏預設滑桿（柔膚／膚色／補光）、重設，以及面板開關觸覺回饋。
- 面板顯示時隱藏底層控制，避免有色按鈕穿透造成光塊。
- 相機權限與 AVFoundation 前置相機預覽骨架；相機設定先 commit，再啟動 session。

## 尚未提供

美顏滑桿只修改本次執行的 UI 參數，尚無影像效果或持久化。尚無 VideoToolbox 編碼、USB/usbmux、CAM1/CUM1、桌面握手或 virtual camera 串接。畫面中的 1080p/30 是預定輸出，不是量測數據。Simulator 的 DEMO 區域是版面佔位，不是相機畫面。

此模板的 Phone Lens 名稱與配色是本次 iOS 設計提案；不取代根目錄 SPEC-v3.md 的既有 Windows／Android 規格。未來對接應遵守該文件的協定與解析度要求。

## 建置

以 Xcode 26+ 開啟 `PhoneLens.xcodeproj`，選擇 `PhoneLens` scheme 與 iPhone Simulator。最低部署版本 iOS 17；實體裝置需自行設定 Signing Team。

從儲存庫根目錄執行：

```sh
xcodebuild -project ios/PhoneLens.xcodeproj -scheme PhoneLens \
  -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath /tmp/phonelens-template-build build CODE_SIGNING_ALLOWED=NO
```

## 驗證範圍

2026-10-21 已在 Xcode 27 / iPhone 14 iOS 27 Simulator 安裝、啟動並目視檢查首頁和美顏面板：控制文字未溢出、三個滑桿同時可見、面板沒有底層彩色光塊。2026-10-23 PR 整理時重新建置。

實體相機、觸覺、舊版 OS fallback、美顏效能與手機至桌面視訊鏡頭全鏈路尚未驗收。重測 UI 時應檢查：美顏面板開關、滑桿數字同步、重設回 35/20/12、連線頁誠實呈現功能未接入，以及關閉面板後底部控制恢復。
