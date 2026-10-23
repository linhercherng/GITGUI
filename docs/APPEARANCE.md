# 外觀與圖示

在主視窗右上角選「外觀配色」，按任一配色立即套用並儲存。
三種深色與一種淺色：石墨深色、午夜藍、森林綠、霧白。
所有選項支援鍵盤 Tab／Enter，Esc 關閉視窗。按鈕保留文字，圖示不代替操作名稱。

配色涵蓋表面、文字、選取、主要／危險按鈕、Git 設定、確認與憑證對話框、
Diff、三方編輯器及提交圖。Diff 保留文字 +/- 標記；不只依賴紅綠區分。
主視窗採用 34px 按鈕與 38px 主要提交按鈕；工具列將同步與復原操作分組。
側欄建立／切換按鈕使用短標籤，完整名稱保留在提示與指令面板。

使用者配色 ID 存於 `%LOCALAPPDATA%\GitGui\appearance.json`（其他平台為
LocalApplicationData/GitGui），在主視窗建立之前讀取，避免先顯示錯誤主題。
小型 JSON 透過同目錄暫存檔與原子替換寫入。損壞、過大或不認識的偏好檔回到石墨，
磁碟／權限錯誤會留在選擇視窗，原主題不變；不會修改 Git 設定、倉庫或憑證。

圖示為本專案原創的 24-unit 向量線條，不依賴 emoji、圖示字型或新增 NuGet 套件。
應用程式的分支圖示有 16/24/32/48/64/128/256px ICO 與 256px PNG，
同時用於 Windows 執行檔、視窗與主視窗識別。可用
`./scripts/Generate-AppIcon.ps1` 在 Windows 重建。

## 驗證

- UI 測試涵蓋即時切換、儲存失敗、不明 ID fallback、同主題修復儲存與重啟還原。
- 真實暫存檔測試涵蓋持久化、損壞 JSON 與原子替換失敗時的暫存清理。
- 四款主題的正文、輔助文字、Diff 增刪文字、選取及主要按鈕驗證 WCAG 4.5:1 對比。
- 繁中／英文在 1100px 最小視窗下檢查主要按鈕邊界，既有動作測試保留。
- [圖像證據](images/appearance-picker.png) 由 Avalonia 真實控制項與固定測試資料渲染，
  包括四款工作區與四款提交圖；不是瀏覽器仿製或 AI 示意圖。

```powershell
$env:GITGUI_APPEARANCE_SCREENSHOTS = Join-Path $PWD 'docs/images'
dotnet test tests/GitGui.UI.Tests/GitGui.UI.Tests.csproj -c Release
dotnet test tests/GitGui.Engine.Tests/GitGui.Engine.Tests.csproj -c Release --filter FullyQualifiedName~AppearancePreferenceTests
```

實作參考：[Avalonia 動態資源](https://docs.avaloniaui.net/docs/app-development/resource-dictionary)。
`AppearanceManager` 統一管理語意資源與 Fluent 的深／淺變體；自繪提交圖透過
StyledProperty 更新畫面，Core 僅定義偏好儲存介面。
