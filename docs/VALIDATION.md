# M1 驗證紀錄

日期：2026-10-23。此紀錄區分本機實測、Headless 渲染與遠端 CI；完整功能對照見 [M1-ACCEPTANCE.md](M1-ACCEPTANCE.md)。

## 本機環境與結果

- Windows、.NET SDK 8.0.404、Git 2.54.0.windows.1。
- `dotnet restore GitGui.sln --locked-mode`：通過。
- `dotnet build GitGui.sln -c Release --no-restore`：0 warnings / 0 errors。
- 完整 Release 回歸：111 passed / 0 failed / 0 skipped（Core 15、UI 20、Engine 76）；另兩個新增 Preview 案例通過，合計覆蓋 113 項。最後的 SHA 搜尋修正另以 GraphHistoryTests 4 項重跑通過。
- `dotnet list GitGui.sln package --vulnerable --include-transitive`：當次 NuGet 來源未回報已知漏洞。
- `tests/WindowsSmoke.ps1 -Desktop`：實際 Windows WinExe AskPass Unicode 往返通過；桌面視窗存在、Responding=true，正常關閉。
- 三張 Avalonia Headless/Skia 截圖已人工檢視：[工作區](images/m1-headless.png)、[提交圖](images/m1-headless-history.png)、[三方衝突編輯](images/m1-headless-conflict.png)。截圖使用固定測試資料。
- 原始 specification.md 與使用者附件的 SHA-256 相同：`4EBC58C7CD23D2F0715471FD65FA86628C995E82F91E751519F2EB9A8EA07369`。

## 真實 Git 驗證

使用獨立暫存目錄，測試自行建立倉庫、設定測試身分與本地 hooks。未對使用者既有倉庫執行破壞性測試。

- 檔案、hunk、行暫存與反向取消：26 個 patch 案例，包含 CRLF、中文路徑、無尾換行、新／刪除檔與 executable mode。
- 分支與網路：本地 bare remote 的 clone/fetch/pull/push、分支 CRUD、ff-only、stale lease、保護分支確認、hook 拒絕與此次 no-verify。
- commit/amend、merge conflict／ours/theirs／MarkResolved／merge commit、abort、Undo/Redo。
- raw 工作目錄、二進位／未追蹤、SHA-1/SHA-256、HEAD 狀態、各 index stage、所有本地分支/tag 與 GC 後復原。
- 固定 seed 的 20 次變更序列後，逐項比對 refs、index entries/flags 與所有工作檔原始位元組。
- 既有 index.lock 不刪除、refs 命名衝突在修改前失敗、ignored 碰撞保留、Unix executable 衝突編輯不降權限。
- 未信任 filter 不執行；信任後依正常 Git clean filter 比對內容。
- SQLite 持久性、同使用者 AskPass 協定、無效請求後可正常繼續，以及命令秘密遮蔽。

## 審查與修復

分別執行後端安全／規格與 UI 規格／品質審查。已處理：index/ref 鎖取得順序、Windows symlink placeholder、信任設定傳遞、pull.ff 與 rebase 策略確認、FETCH_HEAD 競態、批次物件串流管線阻塞、Unix AskPass 重新接聽競態、Windows 無主控台 AskPass、Unix executable mode、遲到 Diff／commit 回應、載入／儲存時的衝突草稿遺失。

延遲 backend 測試驗證回應過期時不套用舊 patch、不切回舊 commit，且儲存期間較新的草稿不被清除。UI 審查修復後核准；後端未發現剩餘阻擋問題。

首次 Linux CI 發現無效 AskPass 請求結束時重建 listener 會重設已排隊連線；已改為重用 listener 並 Disconnect，回歸案例連續混合 10 次無效／有效請求及 5 個並行正常請求。本機 InfrastructureTests 3 項重跑通過，修正後 Linux 全部 113 項通過。macOS 另發現暫存路徑加上 named pipe 名稱超過 104 bytes，已縮短名稱並保留完整 128-bit 隨機識別碼。

## GitHub 與未驗證範圍

工作流程包含 Windows 2022、Ubuntu 22.04、macOS 14 的 locked restore/build/test，Windows 另跑實際 AskPass apphost。推送後核對本次 commit 的 [Actions](https://github.com/linhercherng/GITGUI/actions/workflows/ci.yml)。

本輪不宣稱達到大型效能指標，也未執行 BenchmarkDotNet。未驗證真實 macOS/Linux 桌面操作、Git 2.40 精確版、reftable 倉庫、各種 GCM/SSH/LFS/proxy/mergetool 組合與跨平台安裝封裝。這些與 README 所列不支援狀態留待後續里程碑。

M0 原本的 18 項測試已併入本輪回歸範圍；不是把 M0 結果當作 M1 功能證據。
