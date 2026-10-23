# 里程碑與驗收

原規格的 M0–M4 圖表未附原圖。以下沿用初始化時的排程分組，M1 功能以 [驗收表](M1-ACCEPTANCE.md) 與 [驗證紀錄](VALIDATION.md) 為準。

| 里程碑 | 狀態／範圍 | 關卡 |
| --- | --- | --- |
| M0：專案基礎 | 已完成：七專案、唯讀 UI、CLI/Lib 路由、SQLite、測試與 CI | 三平台 build/test；唯讀不改 index |
| M1：P0 日常操作 | 已實作：11 項 P0、專業三欄 UI、三方編輯、快照 Undo/Redo | 真實 Git 操作與復原、寫入佇列、安全前置檢查、Headless 與 Windows 實機驗證 |
| M2：P1 進階歷史 | 待做：stash、互動 rebase、cherry-pick/revert/reset、tag 管理、blame、reflog、遠端管理、內建終端機 | 中斷／續作與隨機序列復原；擴大衝突類型 |
| M3：P2 與相容性 | 待做：submodule、worktree 管理、LFS、bisect、sparse、ARM 決策 | 精確 Git 2.40/最新版本、SHA-256/reftable 矩陣、真實桌面驗收 |
| M4：正式版 | 待做：大型效能、完整無障礙／語言資源、封裝與安裝、.NET 升級、授權 | 規格第 11 節目標實測，散佈與升級驗證 |

## M1 後的技術工作

- 依 refs tip 做增量提交快取，精準分流檔案監看刷新；背景自動 fetch 與倉庫級設定。
- 依實測擴大 LibGit2Sharp Diff／篩選讀取；目前 Diff 與篩選走 CLI，優先保留 Git 語意。
- 完整語法高亮、非 UTF-8 檔案的無損文字編輯、無效位元組路徑操作。
- 一般 index 語意之外的可重建 extension／stat cache 不要求 byte-identical；持續擴大快照屬性測試與錯誤注入。
- 快照 SQLite 索引的遺失重建、使用者可調保留政策、備份匯出與磁碟壓力管理。
- 單一實例、終端機、完整錯誤建議動作按鈕與可選的脫敏診斷匯出。
- 10 萬 commit／5 萬檔案效能基準及 10% 回歸門檻；目前未宣稱達標。

## 已完成的安全驗證

快照不再依賴不含 untracked/unmerged 的 stash create。已保存並測試 raw 工作檔、各 index stage、unborn/detached HEAD、分支/tag、merge 狀態與 GC 保護。還原先取得 index/ref 鎖；鎖衝突與 refs 命名衝突在替換資料前失敗。捨棄與還原包含重做快照；ignored 檔案不進行全倉庫清除。

所有網路／寫入使用 CLI。強制推送同時使用 lease/includes；保護分支文字確認、stale lease、hook 拒絕、信任 filters 及 AskPass Unicode 已有測試。未支援的 sparse/submodule/非 merge 進行中狀態會拒絕快照寫入，避免提供無法履行的復原保證。
