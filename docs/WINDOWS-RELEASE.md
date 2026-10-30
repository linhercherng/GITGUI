# Windows x64 免安裝發行

從 [v0.7.2 Release](https://github.com/linhercherng/GITGUI/releases/tag/v0.7.2)
下載 `GITGUI-0.7.2-win-x64-portable.zip`，完整解壓縮後開啟
`GITGUI.vbs`，不需系統管理員權限。支援OS範圍見 [M4驗收](M4-ACCEPTANCE.md)。

## v0.7.2：本機版本還原圖文手冊更新

本版套件收錄 24 章操作手冊與 26 張標示截圖。第 22～24 章涵蓋本機提交與 GPG 錯誤、單檔／整個專案還原、分支建立與合併；第 23.4 節新增 10 張實際還原截圖，標示建立備用分支、選取目標 SHA、Hard Reset、確認執行、檢查還原結果及切回備用版本的點擊位置。HTML、Markdown、圖片與引用文件隨主程式 ZIP 提供，另附獨立離線手冊 ZIP。

v0.7.2 沿用 v0.7.1 公開 ZIP 內的程式、.NET Runtime、完整 Portable Git 與原始授權檔案，不重新編譯或變更產品功能。逐檔核對只允許更新手冊、說明文件、發行中繼資料及新增 10 張標示圖片；`build-info.json` 仍記錄程式版本 0.7.1 與原始來源 `4fdfd9c8c9e74df2ebd0b358ff53766aec0bf763`，`release-info.json` 另記錄套件版本 0.7.2 與本次文件來源。

發布前對新的 ZIP 執行隔離環境下的套件功能測試、AskPass 與桌面啟閉檢查，並檢查離線圖片、連結及 SHA-256；具體結果隨本版 Release 提供。v0.7.1 的 504 項完整回歸與 v0.7.0 的 M4 效能數據是歷史證據，不冒稱為 v0.7.2 重新執行的完整回歸或正式效能量測。舊版 Release 與 ZIP 保留，不覆寫同版附件。

## v0.7.1：已有檔案資料夾初始化

在「倉庫操作」瀏覽原資料夾，填寫可選的倉庫本機姓名／Email，按「初始化倉庫」，再檢查檔案、暫存與提交。保留原檔案與 `.gitignore`；既有倉庫、父倉庫與 bare 倉庫不會被重新初始化，Clone 保持空目錄限制。

Windows 本機 Core 33、Engine 299、UI 172，共 504 項回歸通過；Engine／UI 使用內附 PortableGit 2.56.0.windows.3。UI 測試使用真正的 Avalonia headless 控制項、按鈕的無障礙 Invoke 與真實 Git 後端；其他視窗測試也涵蓋指標／鍵盤操作。實際 ZIP 的 9,969 個檔案與建置輸出逐檔核對，另完成 apphost／native host 的 UTF-8 AskPass，以及隔離設定下的桌面啟動、繪製與結束檢查。

程式來源為 `4fdfd9c8c9e74df2ebd0b358ff53766aec0bf763`，測試同步修正來源為 `2aae7297c549245df48aa65e17298d05855e2e1a`。發布時沿用已驗證的程式、Runtime、Portable Git、授權及圖片，只更新發行文件與新增 `release-info.json`；逐檔核對報告與最終 ZIP SHA-256 隨 Release 提供。`build-info.json` 保留真正二進位來源，不將文件提交冒稱為重新建置來源。

手冊保留 21 章與 16 張標示截圖；第 20.2 節更新為完整 GUI 流程。舊圖未顯示 v0.7.1 新增欄位，文字已明確註記。Release 同時提供包含 HTML、Markdown、圖片與引用文件的獨立離線手冊 ZIP。

此版完成的是功能回歸與套件檢查；未將 v0.7.0 的數據當成本版重新進行的正式效能驗收。先前系統 Git 2.54 與內附 Git 在舊 Windows 刪除模擬案例的結果差異，以及 UI 測試設定／清理的失敗紀錄，保留在驗證附件；最終選定的完整回歸沒有刪除案例或放寬斷言。

## v0.7.0 正式效能驗收紀錄

v0.7.0已通過M4正式效能驗收，原門檻未調整：首次啟動1,319.8566ms、五組捲動全部通過，產品UI工作最大13.9479ms，詳見 [效能結果](M4-PERFORMANCE.md)。

預設方式需要Windows既有的Windows Script Host；環境不允許VBS時可改用 `GitGui.App.exe`。1.5秒啟動驗收使用 `GITGUI.vbs`，不能移用至備用方式。請保留 `GitGui.Host.dll`、`GitGui.App.r2r.dll`及所有隨附檔案。

二進位來源為 `f923f11e18286b78b5b7358212bce5736032d5be`。手冊完成後，以逐檔SHA確認的同一測試映像封裝文件；不重新編譯或替換程式。`build-info.json`保留二進位來源，`release-info.json`與promotion報告記錄文件來源、原測試ZIP及最終ZIP。重新編譯產物若MVID／hash不同，不能沿用此次原生量測。

## 封裝內容

- .NET 10.0.13，自含執行環境，無 trimming／single-file extraction。
- GITGUI M4 與 Avalonia、SQLite、libgit2 原生依賴。
- 完整 PortableGit 2.56.0.3，保留 shell、SSH、helpers、原始授權與版本清單。
- `START-HERE.txt`、繁體中文離線手冊 `USER-GUIDE.zh-TW.html`／`.md`、`THIRD-PARTY.txt`、`licenses/` 與 `build-info.json`。
- Release 另附 SHA256SUMS、封裝驗證報告與 PortableGit copyleft 來源 ZIP。

v0.7.0的手冊保留21章；第20、21章提供首次備份與修改後更新上傳範例，附 16 張真實操作截圖、編號框選與逐步說明。`images/walkthrough/` 隨 ZIP 一起提供，HTML／Markdown 的圖片連結可離線開啟；GitHub 位置參考圖沿用現有 GITGUI 倉庫，與隔離的本機示範提交分別標示。

不修改系統 PATH，不註冊服務。個人設定仍在 `%LOCALAPPDATA%\GitGui`，
這不是把個人憑證與資料庫放進 USB 的模式。Git 作者與遠端登入使用個人設定。
應用程式尚未做 Authenticode 簽章。保留整個資料夾才能搬移或更新。

## 重建與驗證

需要 Windows、PowerShell 7、.NET 10 SDK，從指定版本乾淨 checkout 執行：

```powershell
./scripts/Build-WindowsPortable.ps1 -Version 0.7.0 -OutputDirectory C:/Build/GITGUI-0.7.0
./tests/Test-WindowsPortable.ps1 -Archive C:/Build/GITGUI-0.7.0/GITGUI-0.7.0-win-x64-portable.zip -Desktop
./scripts/Collect-PortableGitSources.ps1 `
  -PortableGit C:/Build/GITGUI-0.7.0/GITGUI-0.7.0-win-x64-portable/PortableGit `
  -OutputDirectory C:/Build/GITGUI-0.7.0-sources
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

升級檢查比對raw HEAD、可見refs、index的OID／mode／stage／flags及每個工作檔案hash；不要求可重建的index stat cache逐位元組相同。M3 fixture在取消LFS追蹤後留下不同的工作樹attributes與舊stat cache，重建index可使Git重新雜湊而改變porcelain標記，即使檔案與index內容完全相同。前後porcelain保留在驗證報告作診斷，不將舊快取的標記當成實際內容；原始fixture仍以完整備份恢復並核對hash。
另外啟動真正的 GitGui.App.exe，驗證 AskPass UTF-8 往返與桌面主視窗啟動／正常關閉。
桌面 smoke 在30秒內輪詢實際視窗就緒，記錄觀察到的時間；這是啟閉功能檢查，並非放寬原規格1.5秒冷啟動門檻。固定等待4秒的舊檢查曾在本機失敗，單獨重現約2秒就緒；嚴格效能結果仍以原生量測全樣本為準。
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
