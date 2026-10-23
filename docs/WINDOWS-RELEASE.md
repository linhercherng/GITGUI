# Windows x64 免安裝發行

從 [M4 預覽版 Release](https://github.com/linhercherng/GITGUI/releases/tag/v0.5.0)
下載 `GITGUI-0.5.0-win-x64-portable.zip`，完整解壓縮後開啟
`GitGui.App.exe`，不需系統管理員權限。支援OS範圍見 [M4驗收](M4-ACCEPTANCE.md)。

v0.5.0列為預覽版：功能與回歸測試通過，大型倉庫首屏及嚴格16ms回應目標仍未完整通過，不能視為M4正式驗收完成。詳見 [效能結果](M4-PERFORMANCE.md)。

## 封裝內容

- .NET 10.0.13，自含執行環境，無 trimming／single-file extraction。
- GITGUI M4 與 Avalonia、SQLite、libgit2 原生依賴。
- 完整 PortableGit 2.56.0.3，保留 shell、SSH、helpers、原始授權與版本清單。
- `START-HERE.txt`、繁體中文離線手冊 `USER-GUIDE.zh-TW.html`／`.md`、`THIRD-PARTY.txt`、`licenses/` 與 `build-info.json`。
- Release 另附 SHA256SUMS、封裝驗證報告與 PortableGit copyleft 來源 ZIP。

不修改系統 PATH，不註冊服務。個人設定仍在 `%LOCALAPPDATA%\GitGui`，
這不是把個人憑證與資料庫放進 USB 的模式。Git 作者與遠端登入使用個人設定。
應用程式尚未做 Authenticode 簽章。保留整個資料夾才能搬移或更新。

## 重建與驗證

需要 Windows、PowerShell 7、.NET 10 SDK，從指定版本乾淨 checkout 執行：

```powershell
./scripts/Build-WindowsPortable.ps1 -OutputDirectory C:/Build/GITGUI-0.5.0
./tests/Test-WindowsPortable.ps1 -Archive C:/Build/GITGUI-0.5.0/GITGUI-0.5.0-win-x64-portable.zip -Desktop
./scripts/Collect-PortableGitSources.ps1 `
  -PortableGit C:/Build/GITGUI-0.5.0/GITGUI-0.5.0-win-x64-portable/PortableGit `
  -OutputDirectory C:/Build/GITGUI-0.5.0-sources
```

輸出目錄必須尚不存在，避免覆寫已測試的 ZIP。NuGet 另用各專案的
`packages.win-x64.lock.json` 鎖定；不改動原本跨平台鎖定檔。Git 官方自解壓檔
核對 SHA-256 後執行，讓官方 post-install 正常完成。ZIP API 保留隱藏檔。
來源附件按 `docs/portable/portablegit-sources.json` 的 56 份上游來源與雜湊取得，
核對雜湊並逐份確認 tar 可完整讀取；下載使用暫存檔，避免沿用中斷檔案。

驗證腳本把 ZIP 解到新的中文／空白路徑，發行檔保持原封不動；測試驅動程式
僅疊加到該測試副本，不會進入發行 ZIP。疊加前逐一核對共享 DLL、原生依賴與
執行環境雜湊相同。子行程 PATH 只留 Windows System32，DOTNET_ROOT 指向不存在
的目錄，關閉多層 runtime 搜尋，隔離 Git 全域設定。測試實際自動找到程式旁 Git，
並確認 CLR 由解壓目錄載入。

測試涵蓋 SQLite、原生 libgit2、中文檔名／CRLF、init、stage、commit、shell hook、
amend、branch、快照 HEAD/index/worktree/refs 還原及重做、本地 bare push/clone；
M2 stash（中文 untracked）與堆疊復原、附註 tag、remote CRUD、rebase reword 的
內附 shell/editor helper、blame/reflog，以及持續終端機使用同一套 Portable Git。
M3 驗證工作樹管理、bisect reset、sparse undo、父倉庫 submodule undo 保留子資料、
LFS 真實本地遠端物件傳輸／取出、內附 SSH signer 簽章標籤與 verify-tag，並確認離線手冊與截圖存在。
M4增加實際Big5訊息位元組、無法編碼時HEAD與快照清單均未改變、語系／快捷鍵保存及舊資料表重新開啟。完整本機驗證另以 `-LegacyDatabase` 與 `-LegacyRepository` 傳入M3實際ZIP測試產生的資料；此選項嚴格限制為TEMP內既有的專用smoke fixture。資料庫使用副本，倉庫先完整備份並預建復原副本；在原路徑執行舊快照Undo/Redo，最後保留測試後目錄並恢復完整原始fixture，核對index、檔案、Git物件與原DB hash。快照綁定原根路徑，不能將此選項當成任意搬移快照的工具。一般CI不含該舊版本資料，不能把 `legacyCatalogCount=0` 稱為實測跨版本升級。
另外啟動真正的 GitGui.App.exe，驗證 AskPass UTF-8 往返與桌面主視窗啟動／正常關閉。
Windows CI 每次建置也重建 ZIP 並執行無桌面 smoke test，提供可下載的 CI artifact。

v0.5.0 的六格 Git 相容性 CI 中，只有 Windows 最新 Git 格重建並驗證發行 ZIP；Windows 2.40 格保留其精確 PATH，執行相同完整測試。固定版本與格式覆蓋見 [M3 相容性](M3-COMPATIBILITY.md)。目前正式套件仍為 x64；ARM64 決策見 [說明](M3-ARM-DECISION.md)。

本機初次完整回歸測試在大量來源下載同時執行時，佇列測試的 15 秒同步等待與
失敗清理出現問題；獨立重跑通過。測試改為 60 秒等候，並以 finally 排空暫停中的
操作，避免清理目錄錯誤遮蔽原始測試錯誤。未改動產品操作邏輯或減少斷言。

## 驗證邊界與撤回

本機是在現有 Windows 主機上限制子行程環境，沒有使用全新 Windows VM。
不是所有 SSH agent、LFS、代理、遠端認證或外部 mergetool 組合的驗收。
發行前要求完整回歸與 Windows ZIP 測試成功；驗證報告附在 Release。

發現重大錯誤時撤下有問題的下載或標記為預發行，保留 tag／校驗紀錄，
以新修補版本重新發布；不靜默取代同版 ZIP，不刪除使用者倉庫與資料。
