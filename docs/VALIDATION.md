# M0 驗證紀錄

日期：2026-10-23。這是初始化版本的證據，不代表完整規格或 P0 已完成。

## 本機環境

- Windows、.NET SDK 8.0.404、Git 2.54.0.windows.1。
- `dotnet restore GitGui.sln --locked-mode`：通過。
- `dotnet build GitGui.sln -c Release --no-restore`：0 warnings / 0 errors。
- `dotnet test GitGui.sln -c Release --no-build`：18 passed / 0 failed / 0 skipped。
- Core 6、Engine/Infrastructure 10、UI 2。
- `dotnet list GitGui.sln package --vulnerable --include-transitive`：當次 NuGet 來源未回報已知漏洞。
- 實際啟動 Release Windows EXE，確認 `GITGUI · M0` 視窗存在且 Responding=true，之後正常關閉。
- Avalonia Headless/Skia 產生 [UI 截圖](images/m0-headless.png)，人工檢視三欄配置、中文顯示與 Diff；截圖內容為固定測試資料。

## 測試範圍

- porcelain v2 一般、rename、unmerged、untracked、中文／空格／換行／emoji、無效 UTF-8 路徑位元組與截斷輸出。
- 真實 SHA-1/SHA-256 倉庫的 status、歷史、Diff，前後 index 位元組一致；unborn HEAD 與 staged Diff。
- 外部 fsmonitor／Diff 設定不被讀取流程執行。
- Lib 路由決策、失敗降級、取消傳播、Core 禁止相依。
- SQLite 路徑參數化、重複紀錄與重新連線後的持久性。
- UI 綁定、唯讀 Diff、載入錯誤清除舊狀態、視窗釋放生命週期。

## 審查與已處理問題

- 重複 Dispose 的 UI 測試先失敗，再加入 idempotent disposal 並重跑通過。
- 切換至無效路徑時舊 Diff 殘留的回歸測試先失敗，再清空舊狀態並重跑通過。
- 新增 Skia 中央套件版本後，以 force-evaluate 更新 lock，隨後 locked restore 與完整 build/test 通過。
- Core 不引用 Process、Avalonia、LibGit2Sharp；寫入／網路功能未開放。原規格快照對 untracked/unmerged 的不足已列在 ROADMAP。
- 未將本機路徑、憑證或倉庫內容寫入日誌；SQLite 僅儲存使用者開啟過的路徑。

## 尚未驗證

大型倉庫目標、真實 macOS/Linux 桌面互動、Git 2.40 精確版本、reftable 整合案例、快照完整復原、跨平台封裝與安裝。GitHub runner 的 build/test 結果請查看 [Actions](https://github.com/linhercherng/GITGUI/actions)。

未執行 BenchmarkDotNet 效能量測；目前只建立可執行的 50,000 筆 status 解析基準入口。
