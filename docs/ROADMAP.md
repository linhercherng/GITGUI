# 里程碑與驗收

原規格的 M0–M4 圖表未附原圖，以下是本專案的初始排程提案，並非還原原圖內容。

| 里程碑 | 範圍 | 關卡 |
| --- | --- | --- |
| M0：專案基礎 | 七專案、唯讀 UI、CLI/Lib 路由、SQLite、測試與 CI | 三平台 build/test；真實倉庫唯讀不變更 index |
| M1：P0 日常操作 | init/clone、file/hunk/line stage、commit、分支、Diff、圖形歷史、fetch/pull/push、merge | 所有 P0 可由 UI 完成；每倉庫寫入佇列；中高風險操作可復原 |
| M2：P1 進階歷史 | stash、互動 rebase、cherry-pick/revert/reset、tag、blame、reflog、遠端管理 | 隨機操作序列快照完整復原；衝突／取消／續作驗證 |
| M3：P2 與相容性 | submodule、worktree、LFS、bisect、sparse、簽章、ARM 決策 | 精確 Git 2.40/最新版本與 SHA-256/reftable 矩陣 |
| M4：正式版 | 大型倉庫效能、無障礙、雙語、安裝封裝、遙測同意、升級 .NET | 規格第 11 節實測通過；授權與散佈方式確定 |

## 安全設計待驗證

- `stash create` 不包含未追蹤／忽略檔案。啟用 clean／捨棄前必須另外保存這些內容，或拒絕操作；不得只依賴原規格的指標快照而承諾完整復原。
- unmerged index 無法以一般 `write-tree` 完整表示；必須保存 stage 1/2/3 的 index 資料與狀態，失敗時阻擋破壞性操作。
- snapshot ref 必須同時保護 HEAD、所有分支／tag、index tree、worktree stash 等物件，並在 `git gc --prune=now` 後驗證仍可復原。
- unborn HEAD、detached HEAD、worktree 共用 refs、操作後新建／刪除 refs 需有獨立復原案例。
- 遠端推送無法本地復原；force 僅允許 `--force-with-lease --force-if-includes`，保護分支需輸入名稱確認。
- 啟用寫入與網路前，完成不受信任倉庫設定審查、AskPass、hooks 輸出、秘密遮蔽與 safe.directory 行為測試。

## M0 後的讀取工作

- Byte-oriented log parser、Big5 fallback、各解析器的實際 golden fixture 與 fuzz 測試。
- reftable 整合測試、linked worktree／subdirectory 路徑正規化。
- Commit 詳細 Diff、refs 全分支圖、檔案歷史、blame。
- 300ms debounce 檔案監看、狀態快取、取消 UI、逐筆串流解析、無效位元組路徑選取。
- 細部架構規則（含 UI/Controls 不得互相或反向參照）、像素截圖基準、10% 效能退步門檻。
