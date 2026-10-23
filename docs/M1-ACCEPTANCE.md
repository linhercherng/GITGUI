# M1 P0 驗收對照

日期：2026-10-23。功能驗證採隔離暫存倉庫；UI Headless 測試使用可控 backend，兩者分別證明呈現／命令對映與實際 Git 效果。

| 原規格第 9 節 P0 | UI 入口 | Git／狀態驗證 |
| --- | --- | --- |
| 開啟、clone、init | 路徑列、倉庫操作 | WorkspaceTests、BranchRemoteTests；空目錄保護 |
| 狀態 | 未暫存／已暫存清單 | RepositoryTests、StatusParserTests；SHA-1/SHA-256、特殊路徑 |
| 檔案、hunk、行 stage/unstage | 清單工具列、Patch 選取頁 | PartialPatchTests 26 案例、WorkspaceTests；反向 patch、新／刪除檔、CRLF、無結尾換行與模式 |
| 捨棄 | 選取檔案後確認 | RecoveryWorkflowTests；tracked/untracked 復原與 ignored 重做碰撞 |
| commit/amend | 提交區 | WorkspaceTests；Undo/Redo；BranchRemoteTests hook 拒絕與明示 no-verify |
| 提交圖、搜尋、篩選 | 歷史頁 | GraphLayoutTests、GraphHistoryTests；分支/tag/detached、拓撲順序、隱藏 snapshot refs 與讀取降級 |
| 檔案與 commit Diff | 右側詳情頁 | WorkspaceTests／RepositoryTests；第一筆提交、staged、選取身分；UI bitmap 預覽與過期回應防護 |
| 分支 CRUD | 側欄 | BranchRemoteTests；建立／切換／改名／刪除、刪除後復原、ignored 碰撞拒絕 |
| fetch/pull | 上方工具列 | BranchRemoteTests；local bare remote、tracking、pull.ff=only |
| push | 上方工具列 | BranchRemoteTests；真實 Git transport、保護分支文字確認、stale lease 不覆蓋遠端 |
| merge／衝突 | 衝突橫幅與三方編輯頁 | BranchRemoteTests、RecoveryWorkflowTests、ConflictMarkerTests；ours/theirs/逐段/手編、執行權限、MarkResolved、merge commit、abort 與復原 |

## 全域復原

- SnapshotTests：SHA-1/SHA-256、raw CRLF、二進位、untracked、所有本地 refs、unborn/detached、三個 conflict index stages、merge 標記及 GC 後可復原。
- SnapshotSafetyTests：index.lock 保留、refs 命名衝突在變更前失敗、symlink placeholder、保留政策、未信任 filter 不執行與信任後語意正確。
- RandomRecoveryTests：固定 seed 產生 20 次內容／暫存／提交變更並增刪 refs；GC 後逐檔比對原始位元組、index entries、intent-to-add／assume-unchanged／skip-worktree 與 refs。
- RecoveryWorkflowTests：佇列序列化與等待取消、捨棄復原／重做、衝突文字儲存後復原。
- InfrastructureTests：SQLite 重開持久性、AskPass Unicode 往返、壞請求不妨礙後續憑證請求。
- WindowsSmoke.ps1：實際 WinExe helper 經 named pipe／stdout 交握；另可加 -Desktop 驗證視窗啟動、回應與正常關閉。

## UI 防護與外觀

Headless 測試檢查所有 P0 對映、雙語、圖形選取、復原／重做、確認與秘密輸入、錯誤清空、遲到 Diff／commit 回應、草稿跨刷新保留、延遲衝突載入／儲存、binary guard 及圖片資源釋放。三張實際控制項截圖人工檢視過工作區、歷史圖與三方編輯配置。

## 未納入本輪保證

P1/P2、安裝封裝、完整程式語言高亮、完整無障礙、大型效能門檻、Git 2.40 精確版／reftable 實際整合，以及真實 macOS/Linux 桌面操作。其他工作樹／submodule／sparse／進行中的非 merge 操作按 README 的前置限制處理；不以略過快照冒險執行。
